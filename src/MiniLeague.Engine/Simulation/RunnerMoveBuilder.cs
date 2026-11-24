using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

/// <summary>
/// Works out what happened to each runner on a resolved play, from the position before it and the result.
/// For display only: it never changes the result, and uses no randomness.
/// </summary>
public static class RunnerMoveBuilder
{
    /// <param name="before">The game as it stood before the play (bases and batter not yet updated).</param>
    public static List<RunnerMove> Build(GameState before, PlayResult result)
    {
        bool plateAppearance = result.Outcome is not (OutcomeType.StolenBase or OutcomeType.CaughtStealing);

        // Lead runner first, so a run is credited to the runner closest to home.
        var participants = new List<(Player Player, int From)>();
        if (before.RunnerOnThird is { } third) participants.Add((third.Player, 3));
        if (before.RunnerOnSecond is { } second) participants.Add((second.Player, 2));
        if (before.RunnerOnFirst is { } first) participants.Add((first.Player, 1));
        if (plateAppearance) participants.Add((before.CurrentBatter, 0));

        BaseRunner?[] after = [result.NewFirst, result.NewSecond, result.NewThird];
        int runsLeft = result.RunsScored;
        var moves = new List<RunnerMove>(participants.Count);

        foreach (var (player, from) in participants)
        {
            if (OutFor(before, result, from) is { } outMove)
            {
                moves.Add(new RunnerMove(player, from, outMove.ToBase, outMove.Fate));
                continue;
            }

            int to = Array.FindIndex(after, r => ReferenceEquals(r?.Player, player)) + 1;  // 0 when not on a base
            if (to > 0)
                moves.Add(new RunnerMove(player, from, to, to == from ? RunnerFate.StayedPut : RunnerFate.Safe));
            else if (runsLeft > 0)
            {
                runsLeft--;
                moves.Add(new RunnerMove(player, from, 4, RunnerFate.Scored));
            }
            else
                // Not out, not on a base, no run: the inning ended on the play and he was left on base.
                moves.Add(new RunnerMove(player, from, from, RunnerFate.StayedPut));
        }
        return moves;
    }

    // Who the engine retires on each kind of play (everyone else is safe or scores).
    private static (int ToBase, RunnerFate Fate)? OutFor(GameState before, PlayResult result, int from) => (result.Outcome, from) switch
    {
        (OutcomeType.Strikeout or OutcomeType.FlyOut or OutcomeType.LineOut or OutcomeType.PopOut, 0) => (0, RunnerFate.Retired),
        (OutcomeType.GroundOut or OutcomeType.DoublePlay or OutcomeType.SacrificeBunt, 0) => (1, RunnerFate.ForcedOut),
        (OutcomeType.DoublePlay, 1) => (2, RunnerFate.ForcedOut),
        (OutcomeType.CaughtStealing, 1) => (2, RunnerFate.TaggedOut),
        // A hit and run that goes wrong: the batter struck out and the runner was thrown out at second, or the batter
        // lined out and the runner, already off the bag, was doubled off first (he is out at the base he started from).
        (OutcomeType.Strikeout, 1) when result.OutsRecorded >= 2 => (2, RunnerFate.TaggedOut),
        (OutcomeType.LineOut, 1) when result.OutsRecorded >= 2 => (1, RunnerFate.TaggedOut),
        // A fielder's choice: the defense took the lead runner it could, at the next base. He was forced when he had
        // the batter or a runner behind him pushing him along; a lone runner on second is tagged.
        (OutcomeType.FieldersChoice, > 0) when from == BuntModel.FieldersChoiceFrom(
                before.RunnerOnFirst is not null, before.RunnerOnSecond is not null, before.RunnerOnThird is not null)
            => (from + 1, from == 1 || before.RunnerOnFirst is not null ? RunnerFate.ForcedOut : RunnerFate.TaggedOut),
        _ => null,
    };
}
