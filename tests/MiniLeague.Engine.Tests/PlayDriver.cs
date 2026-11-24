using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;

namespace MiniLeague.Engine.Tests;

/// <summary>Plays computer turns for tests that sample many plays, making sure steal attempts are among them.</summary>
internal static class PlayDriver
{
    // WHY: These tests need stolen bases and caught stealings to look at. The computer used to produce them as a
    // side effect of hit-and-run calls meeting its own pitchouts, which was a bug; it now steals only when the
    // numbers favor it. Sending the runner on some opportunities keeps those plays in the sample without
    // depending on how adventurous the computer manager happens to be.
    internal static PlayResult? Next(GameService game)
    {
        var s = game.State;
        bool runnerGoes = s is { Status: GameStatus.InProgress, PendingInjury: null }
            && !game.IsHumanTurn
            && s.RunnerOnFirst is not null && s.RunnerOnSecond is null && s.Outs < 2
            && (s.CurrentBatterIndex + s.CurrentInning) % 3 == 0;
        return runnerGoes
            ? game.ExecutePlay(new OffensiveDecision { Strategy = OffensiveStrategy.Steal })
            : game.ExecuteComputerTurn();
    }
}
