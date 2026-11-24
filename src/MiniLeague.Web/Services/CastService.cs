using Microsoft.JSInterop;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;

namespace MiniLeague.Web.Services;

public enum CastStartResult { Started, NoDevices, Cancelled, Failed }

/// <summary>
/// Shows the game on a TV: the browser casts the /tv page to a Cast screen (js/cast.js) and this service keeps
/// that page fed with the current game.
/// </summary>
public sealed class CastService(IJSRuntime js) : IAsyncDisposable
{
    private DotNetObjectReference<CastService>? _self;

    /// <summary>The browser can cast a page (Chrome and Edge, over HTTPS or localhost).</summary>
    public bool IsSupported { get; private set; }
    public bool IsCasting { get; private set; }

    /// <summary>
    /// Casting from the Cast button has failed (or cannot be tried), so the menu offers the "TV view" page as a
    /// fallback: open it and cast that tab from the browser's own menu.
    /// </summary>
    // WHY: the TV view is a workaround, not a feature to show everyone: it stays out of the menu until the Cast button
    // has failed. A browser that cannot cast at all (no Cast button) counts as a failure, or those people could never
    // find the fallback the README and docs/chromecast-support.md send them to.
    // NOTE: "initialized" keeps the item from flashing on screen for the moment before the browser has answered.
    public bool ShowTvFallback => _initialized && (!IsSupported || _castFailed);

    public event Action? Changed;

    private bool _initialized;
    private bool _castFailed;

    public async Task InitAsync()
    {
        if (_self is not null) return;
        _self = DotNetObjectReference.Create(this);
        try { IsSupported = await js.InvokeAsync<bool>("mlbCast.init", _self); }
        catch (JSException) { IsSupported = false; }
        _initialized = true;
        Changed?.Invoke();
    }

    /// <summary>Opens the browser's screen picker. Must run from a click.</summary>
    public async Task<CastStartResult> StartAsync()
    {
        var result = await TryStartAsync();
        // NOTE: "none" (no screen in the picker) fails too: the README tells that person to cast the TV view tab instead.
        // A cancelled picker is a choice, not a failure; a start that works clears an earlier failure.
        var failed = result is CastStartResult.Failed or CastStartResult.NoDevices;
        if (result != CastStartResult.Cancelled && failed != _castFailed)
        {
            _castFailed = failed;
            Changed?.Invoke();
        }
        return result;
    }

    private async Task<CastStartResult> TryStartAsync()
    {
        try
        {
            return await js.InvokeAsync<string>("mlbCast.start") switch
            {
                "started" => CastStartResult.Started,
                "none" => CastStartResult.NoDevices,
                "cancelled" => CastStartResult.Cancelled,
                _ => CastStartResult.Failed,
            };
        }
        catch (JSException) { return CastStartResult.Failed; }
    }

    public async Task StopAsync()
    {
        try { await js.InvokeVoidAsync("mlbCast.stop"); }
        catch (JSException) { }
    }

    /// <summary>
    /// Hands the TV the game as it stands. Cheap enough to call after every change. <paramref name="detailed"/> is
    /// whether the animated field is showing; the TV then shows it too, playing the same plays (js/cast.js).
    /// </summary>
    public async Task PublishAsync(GameState state, bool detailed)
    {
        // WHY: casting is a bonus on top of the game: a failing browser call must never interrupt play.
        try { await js.InvokeVoidAsync("mlbCast.send", Encode(state), detailed); }
        catch (JSException) { }
    }

    /// <summary>The game as the TV needs it: a save without the long history (the TV shows the field and the last play).</summary>
    public static string Encode(GameState state)
    {
        var snapshot = GameSnapshots.Capture(state);
        // WHY: this goes over the wire after every play, and the TV shows only the field, the score and the last
        // play: the history a save keeps (up to 200 plays, the recap and the box score) would only make every message larger.
        // NOTE: Capture shares the live Box object with the game (it does not copy it); `snapshot.Box = new()` only
        // replaces this snapshot's reference, so the real game's box score is untouched.
        snapshot.PlayByPlay = [.. snapshot.PlayByPlay.Take(1)];
        snapshot.Recap = [];
        snapshot.Box = new();
        return GameSnapshots.ToJson(snapshot);
    }

    [JSInvokable]
    public void OnCastChanged(bool available, bool connected)
    {
        // NOTE: GameRoom and CastButton listen to Changed to redraw: IsCasting decides whether the page keeps its own field.
        IsCasting = connected;
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        _self?.Dispose();
        return ValueTask.CompletedTask;
    }
}
