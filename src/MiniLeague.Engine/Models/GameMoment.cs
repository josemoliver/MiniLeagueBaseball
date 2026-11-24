namespace MiniLeague.Engine.Models;

/// <summary>
/// Something worth a tune from the ballpark organist. The engine announces them as they happen
/// (see GameService.TakeMoments); the app decides what to play.
/// </summary>
public enum GameMoment
{
    /// <summary>The home half of the seventh inning is about to start.</summary>
    SeventhInningStretch,

    /// <summary>A team has just scored its third run of a half-inning.</summary>
    Rally,

    /// <summary>A new pitcher has come into the game.</summary>
    PitchingChange,

    /// <summary>A manager has been thrown out.</summary>
    ManagerEjected,
}
