using Microsoft.JSInterop;

namespace MiniLeague.Web.Services;

/// <summary>Which field the game room shows: the original overhead view, or the animated one.</summary>
public enum FieldViewMode { Classic, Detailed }

/// <summary>How fast the animated field plays a play.</summary>
public enum AnimationSpeed { Fast, Normal, Slow }

/// <summary>
/// The player's choice of field view and animation speed, kept in this browser's storage (not in saved games).
/// Detailed is the default (Classic is one click away). Storage can be missing or blocked, so a failure just means the defaults are used.
/// </summary>
public class FieldViewPreferences(IJSRuntime js)
{
    internal const string ViewKey = "mlb-fieldview";
    internal const string SpeedKey = "mlb-animspeed";

    public FieldViewMode View { get; private set; } = FieldViewMode.Detailed;
    public AnimationSpeed Speed { get; private set; } = AnimationSpeed.Normal;

    /// <summary>How many times faster than Normal the animation plays.</summary>
    public static double Factor(AnimationSpeed speed) => speed switch
    {
        AnimationSpeed.Fast => 2.0,
        AnimationSpeed.Slow => 0.65,
        _ => 1.0,
    };

    public double SpeedFactor => Factor(Speed);

    public async Task InitAsync()
    {
        View = Parse(await ReadAsync(ViewKey), FieldViewMode.Detailed);

        // Someone who has asked their device for less motion starts at Fast (and the picture itself drops the
        // sparkle and trails); a speed they have chosen themselves always wins.
        string? stored = await ReadAsync(SpeedKey);
        Speed = Enum.TryParse<AnimationSpeed>(stored, ignoreCase: true, out var chosen) && Enum.IsDefined(chosen)
            ? chosen
            : await PrefersReducedMotionAsync() ? AnimationSpeed.Fast : AnimationSpeed.Normal;
    }

    private async Task<bool> PrefersReducedMotionAsync()
    {
        try { return await js.InvokeAsync<bool>("mlbAnimator.prefersReducedMotion"); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException) { return false; }
    }

    public async Task SetViewAsync(FieldViewMode view)
    {
        View = view;
        await WriteAsync(ViewKey, view.ToString().ToLowerInvariant());
    }

    public async Task SetSpeedAsync(AnimationSpeed speed)
    {
        Speed = speed;
        await WriteAsync(SpeedKey, speed.ToString().ToLowerInvariant());
    }

    private static T Parse<T>(string? text, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : fallback;

    private async Task<string?> ReadAsync(string key)
    {
        try { return await js.InvokeAsync<string?>("mlbStorage.get", key); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException) { return null; }
    }

    private async Task WriteAsync(string key, string value)
    {
        try { await js.InvokeAsync<bool>("mlbStorage.set", key, value); }
        catch (Exception ex) when (ex is JSException or InvalidOperationException) { }
    }
}
