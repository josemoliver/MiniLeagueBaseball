using Microsoft.JSInterop;
using MiniLeague.Engine.Models;

namespace MiniLeague.Web.Services;

/// <summary>
/// Plays synthesized sound effects via wwwroot/js/sound.js (Web Audio API).
/// All calls are best-effort: audio failures never affect gameplay.
/// </summary>
public class SoundService(IJSRuntime js)
{
    public bool Muted { get; private set; }

    public async Task InitAsync()
    {
        try { Muted = await js.InvokeAsync<bool>("mlbSound.isMuted"); }
        catch (JSException) { }
    }

    public async Task SetMutedAsync(bool muted)
    {
        Muted = muted;
        try { await js.InvokeVoidAsync("mlbSound.setMuted", muted); }
        catch (JSException) { }
    }

    /// <summary>
    /// The ballpark-organ theme for the startup page. It plays at most once per launch, and if the browser
    /// has not yet allowed audio it waits for the visitor's first click, key press or tap.
    /// </summary>
    public async Task PlayThemeAsync()
    {
        try { await js.InvokeVoidAsync("mlbSound.playTheme"); }
        catch (JSException) { }
    }

    // Most important first: only one organ tune plays at a time.
    private static readonly GameMoment[] MomentPriority =
        [GameMoment.ManagerEjected, GameMoment.SeventhInningStretch, GameMoment.Rally, GameMoment.PitchingChange];

    /// <summary>The organ tune for a moment in the game.</summary>
    public static string TuneFor(GameMoment moment) => moment switch
    {
        GameMoment.SeventhInningStretch => "stretch",   // Take Me Out to the Ball Game
        GameMoment.Rally => "rally",                    // the Mexican Hat Dance
        GameMoment.PitchingChange => "entrance",        // Hail to the Chief
        GameMoment.ManagerEjected => "funeral",         // Chopin's Funeral March
        _ => "",
    };

    /// <summary>How long after a play's own sound the organ tune starts.</summary>
    private const double TuneDelaySeconds = 1.0;

    /// <summary>
    /// Plays the tune for the most important of the moments, a second after the play's own sound.
    /// If several happened together, the others are skipped rather than played over each other.
    /// Returns how long the tune will take to finish, counted from now (zero if nothing played).
    /// </summary>
    public async Task<TimeSpan> PlayMomentsAsync(IEnumerable<GameMoment> moments)
    {
        var set = moments.ToHashSet();
        var pick = MomentPriority.FirstOrDefault(set.Contains);
        if (set.Count == 0 || TuneFor(pick) is not { Length: > 0 } tune) return TimeSpan.Zero;

        try
        {
            double seconds = await js.InvokeAsync<double>("mlbSound.play", tune, TuneDelaySeconds);
            return seconds > 0 ? TimeSpan.FromSeconds(TuneDelaySeconds + seconds) : TimeSpan.Zero;
        }
        catch (JSException) { return TimeSpan.Zero; }
    }

    public async Task PlayAsync(string name)
    {
        try { await js.InvokeVoidAsync("mlbSound.play", name); }
        catch (JSException) { }
    }

    public async Task PlayForResultAsync(PlayResult r)
    {
        await PlayAsync(r.Outcome switch
        {
            OutcomeType.HomeRun => "homerun",
            OutcomeType.Single or OutcomeType.Double or OutcomeType.Triple => "hit",
            OutcomeType.Strikeout => "strikeout",
            OutcomeType.DoublePlay or OutcomeType.TriplePlay => "doubleplay",
            OutcomeType.Walk or OutcomeType.HitByPitch or OutcomeType.IntentionalWalk => "walk",
            OutcomeType.StolenBase or OutcomeType.WildPitch or OutcomeType.PassedBall or OutcomeType.Balk => "steal",
            OutcomeType.CaughtStealing => "caught",
            OutcomeType.Error => "error",
            _ => "out"
        });

        if (r.RunsScored > 0 && r.Outcome != OutcomeType.HomeRun)
            await PlayAsync("cheer");
    }
}
