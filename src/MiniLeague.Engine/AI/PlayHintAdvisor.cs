using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.AI;

public enum PlayHintSide { Offense, Defense }

public sealed record PlayHint(
    PlayHintSide Side, string Situation, string Matchup, string PitcherNote, string PlatoonNote,
    string LeverageNote, string Call, string Reason, string? Conditional, string ReasonId,
    OffensiveStrategy? Strategy = null, PitchType? Pitch = null, DefensiveAlignment? Alignment = null,
    bool IntentionalWalk = false);

/// <summary>Read-only, repeatable advice from the Analyst's existing play valuations.</summary>
public static class PlayHintAdvisor
{
    public static PlayHint Get(GameState state, PlayHintSide side, DefensiveAlignment? visibleAlignment = null)
    {
        if (state.Status != GameStatus.InProgress) throw new ArgumentException("Hints need a game in progress.", nameof(state));
        var pitcher = state.CurrentPitcher;
        var condition = pitcher.Condition;
        try
        {
            var manager = new ComputerManager(new Random(0), ManagerTuning.Default);
            var batter = state.CurrentBatter;
            var matchup = $"{batter.Name} faces {pitcher.Player.Name}.";
            bool batterHasHand = batter.BatsAgainst(pitcher.Player.ThrowHand) == BatHand.Left
                ? pitcher.Player.ThrowHand == ThrowHand.Right : pitcher.Player.ThrowHand == ThrowHand.Left;
            string platoon = batterHasHand ? "The hitter has the hand advantage." : "The pitcher has the hand advantage.";
            string pitcherNote = pitcher.EntryModifier < 1 && pitcher.BattersFaced - pitcher.SettleFrom < FatigueModel.SettleInBatters
                ? "The pitcher is not yet loose."
                : condition switch
                {
                    PitcherCondition.Fresh => "The pitcher is fresh.",
                    PitcherCondition.Ok => "The pitcher is holding up.",
                    PitcherCondition.Tiring => "The pitcher is starting to tire.",
                    PitcherCondition.Tired => "The pitcher is tired.",
                    _ => "The pitcher is running on fumes.",
                };
            string leverage = Leverage(state, side);
            string situation = Situation(state);
            if (side == PlayHintSide.Offense)
            {
                var decision = manager.MakeHintOffensiveDecision(state,
                    visibleAlignment ?? (state.IsTopOfInning ? state.HomeAlignment : state.VisitorAlignment));
                var (call, reason, conditional, id) = OffensiveText(state, decision.Strategy, visibleAlignment, condition);
                return new(side, situation, matchup, pitcherNote, platoon, leverage,
                    call, reason, conditional, id, Strategy: decision.Strategy);
            }
            else
            {
                var decision = manager.MakeHintDefensiveDecision(state);
                var (call, reason, conditional, id) = DefensiveText(state, decision, condition);
                if (!decision.IntentionalWalk)
                    reason = decision.PitchType switch
                    {
                        PitchType.Fastball => "Challenge him with the fastball. " + reason,
                        PitchType.Curveball => "Make him handle the curveball. " + reason,
                        PitchType.Slider => "Make him handle the slider. " + reason,
                        _ => "Change his timing with the changeup. " + reason,
                    };
                return new(side, situation, matchup, pitcherNote, platoon, leverage,
                    call, reason, conditional, id, Pitch: decision.PitchType,
                    Alignment: decision.Alignment, IntentionalWalk: decision.IntentionalWalk);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException
            or IndexOutOfRangeException or DivideByZeroException)
        {
            // WHY: a partially restored matchup should still offer a safe, selectable call instead of breaking the panel.
            string situation = Situation(state);
            string matchup = "Read the matchup on the field before you commit.";
            if (side == PlayHintSide.Offense)
                return new(side, situation, matchup, "", "", "", "Swing away.",
                    "Keep the out and let the hitter take his turn.", null, "fallback-offense", Strategy: OffensiveStrategy.SwingAway);
            return new(side, situation, matchup, "", "", "", "Throw a fastball and keep a normal defense.",
                "Cover the field and make the hitter put the ball in play.", null, "fallback-defense",
                Pitch: PitchType.Fastball, Alignment: DefensiveAlignment.Standard);
        }
        finally
        {
            // WHY: FatigueModel.GetModifier updates the displayed condition while valuing calls; a hint cannot change the game.
            pitcher.Condition = condition;
        }
    }

    private static (string Call, string Reason, string? Conditional, string Id) OffensiveText(
        GameState s, OffensiveStrategy strategy, DefensiveAlignment? visibleAlignment, PitcherCondition condition)
    {
        var batter = s.CurrentBatter;
        return strategy switch
        {
            OffensiveStrategy.Steal => (RunningRules.StealPlanFor(s) switch
                {
                    StealPlan.Third => "Steal third.", StealPlan.Double => "Send both runners on a double steal.",
                    _ => "Steal second."
                }, "The runner has a good opening, and taking the next base is worth the risk of an out.",
                "If the defense starts watching the runner closely, let the hitter take his turn instead.", "steal"),
            OffensiveStrategy.HitAndRun => ("Call the hit and run.",
                "This hitter can put the ball in play while the runner gets moving. A missed ball can still cost us the runner.",
                "If you would rather protect the runner, swing away and keep him at first.", "hit-and-run"),
            OffensiveStrategy.SacrificeBunt => ("Lay down the sacrifice.",
                "Move the runner into better scoring position and let the next hitter work with it. We are paying for that base with an out.",
                visibleAlignment is DefensiveAlignment.CornersIn
                    ? "With the corners already in, swinging away keeps the bunt from becoming an easy out."
                    : "If they bring the corners in, reconsider the bunt and let him swing.", "sacrifice"),
            OffensiveStrategy.Squeeze => ("Call the squeeze.",
                "The runner breaks for home with the pitch. It can bring in the run, but a missed bunt leaves him exposed.",
                "A safety squeeze keeps the runner at third until the bunt is down.", "squeeze"),
            OffensiveStrategy.SafetySqueeze => ("Call the safety squeeze.",
                "Have the runner wait until the bunt is down. It is the steadier way to try for the run from third.",
                "The full squeeze sends him with the pitch and puts more at risk.", "safety-squeeze"),
            OffensiveStrategy.SwingAgressive => ("Swing away and run aggressively.",
                "There is an extra base to take if the ball finds grass, but do not forget the chance of being thrown out.",
                null, "aggressive"),
            _ when s.RunnerOnFirst is not null && s.Outs < 2 => ("Swing away.",
                "Keep the runner aboard and make the hitter earn the next base. Giving away an out here asks too much.",
                null, "swing-runner"),
            _ when condition is PitcherCondition.Tiring or PitcherCondition.Tired or PitcherCondition.Exhausted =>
                ("Swing away.", "The pitcher is fading. Make him face this hitter instead of handing him an out.", null, "swing-fatigue"),
            _ when batter.PowerRating >= 3 => ("Swing away.",
                "This hitter can do damage with one good swing. Let him use the bat.", null, "swing-power"),
            _ when s.RunnerOnThird is not null && s.Outs < 2 => ("Swing away.",
                "A ball in play can bring the runner home without risking him on a bunt.",
                "A safety squeeze is there if you want the runner to wait for the bunt.", "swing-third"),
            _ when s.RunnerOnSecond is not null => ("Swing away.",
                "The runner is already in scoring position. Keep the out and give the hitter a chance to bring him home.",
                null, "swing-scoring-position"),
            _ when s.Outs == 2 => ("Swing away.",
                "There is no out left to trade for a base. Let the hitter finish the inning with his bat.",
                null, "swing-two-out"),
            _ when s.Outs == 0 => ("Swing away.",
                "Start the inning with the bat. There is nobody to move with a bunt.",
                null, "swing-leadoff"),
            _ when batter.Overall?.BattingAverage >= 0.270 => ("Swing away.",
                "This hitter knows how to put the ball in play. Give him room to work.",
                null, "swing-contact"),
            _ => ("Swing away.", "There is no runner to move. Keep the out and let him hit.", null, "swing-open"),
        };
    }

    private static (string Call, string Reason, string? Conditional, string Id) DefensiveText(
        GameState s, DefensiveDecision decision, PitcherCondition condition)
    {
        if (decision.IntentionalWalk)
            return ("Press IBB to put the hitter on.",
                "This hitter is the bigger danger. Take the open base and make the next man beat us.",
                "The walk gives them another runner, so keep an eye on the force play.", "intentional-walk");
        string alignment = decision.Alignment switch
        {
            DefensiveAlignment.InfieldIn => "bring the infield in",
            DefensiveAlignment.CornersIn => "bring the corners in",
            DefensiveAlignment.GuardLines => "guard the lines",
            DefensiveAlignment.ShiftPull => "shift to the pull side",
            DefensiveAlignment.ShiftOpposite => "shift to the opposite field",
            _ => "keep a normal defense",
        };
        string call = $"Throw a {decision.PitchType.ToString().ToLowerInvariant()} and {alignment}.";
        if (decision.Alignment == DefensiveAlignment.InfieldIn)
            return (call, "Cut down the runner at the plate if the ball stays on the ground. A ball through the drawn-in field is the price.",
                null, "infield-in");
        if (decision.Alignment == DefensiveAlignment.CornersIn)
            return (call, "Be ready to take away the bunt. The corners give up ground behind them.", null, "corners-in");
        if (decision.Alignment == DefensiveAlignment.GuardLines)
            return (call, "Keep a late extra-base hit from slipping down the line. The middle opens up a little.", null, "guard-lines");
        if (decision.Alignment is DefensiveAlignment.ShiftPull or DefensiveAlignment.ShiftOpposite)
            return (call, "Put the gloves where this matchup gives us the best chance, while accepting the open ground elsewhere.",
                null, decision.Alignment == DefensiveAlignment.ShiftPull ? "shift-pull" : "shift-opposite");
        if (s.RunnerOnFirst is not null && s.Outs < 2)
            return (call, "Keep the middle set for a ball on the ground and the chance to turn two.", null, "normal-double-play");
        if (condition is PitcherCondition.Tiring or PitcherCondition.Tired or PitcherCondition.Exhausted)
            return (call, "The pitcher is losing some sharpness. Keep the fielders in their usual places and make the batter hit it.",
                null, "normal-fatigue");
        if (s.CurrentBatter.PowerRating >= 3)
            return (call, "Respect this hitter's power without opening a needless hole in the field.", null, "normal-power");
        if (s.Outs == 2)
            return (call, "With two gone, keep the whole field covered and make him find a gap.", null, "normal-two-out");
        if (s.RunnerOnSecond is not null || s.RunnerOnThird is not null)
            return (call, "A runner is already in scoring position. Keep the middle covered and make the hitter earn the run.",
                null, "normal-scoring-position");
        if (s.RunnerOnFirst is not null)
            return (call, "Hold the ordinary field for a ground ball and keep the force play in reach.", null, "normal-force");
        if (s.Outs == 0)
            return (call, "Cover the whole field to start the inning. No runner is forcing us to bring anyone in.",
                null, "normal-leadoff");
        return (call, "The ordinary field covers the most ground against this hitter. Make him beat it.", null, "normal-open");
    }

    private static string Situation(GameState s)
    {
        string half = s.IsTopOfInning ? "Top" : "Bottom";
        string inning = s.CurrentInning <= 12 ? "the " + new[] { "", "first", "second", "third", "fourth", "fifth", "sixth",
            "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth" }[s.CurrentInning] : "extra innings";
        string outs = s.Outs switch { 0 => "no outs", 1 => "one out", _ => "two outs" };
        string bases = (s.RunnerOnFirst is not null, s.RunnerOnSecond is not null, s.RunnerOnThird is not null) switch
        {
            (false, false, false) => "bases empty", (true, true, true) => "bases loaded",
            (true, true, false) => "runners on first and second", (true, false, true) => "runners on first and third",
            (false, true, true) => "runners on second and third", (true, false, false) => "runner on first",
            (false, true, false) => "runner on second", _ => "runner on third",
        };
        return $"{half} of {inning}, {outs}, {bases}.";
    }

    private static string Leverage(GameState s, PlayHintSide side)
    {
        if (ManagerDecisionModel.Leverage(s) < 0.3) return "A run will barely change the result now.";
        int margin = s.IsTopOfInning ? s.VisitorTotalScore - s.HomeTotalScore : s.HomeTotalScore - s.VisitorTotalScore;
        int runners = (s.RunnerOnFirst is null ? 0 : 1) + (s.RunnerOnSecond is null ? 0 : 1) + (s.RunnerOnThird is null ? 0 : 1);
        if (side == PlayHintSide.Defense)
            return s.CurrentInning >= 7 && Math.Abs(margin) <= 2
                ? "A late run here could swing the game." : "Keep this inning under control.";
        if (s.CurrentInning >= 7 && margin <= 0 && -margin <= runners + 1) return "The tying or go-ahead run is in reach.";
        if (s.CurrentInning >= 7 && margin is 1 or 2) return "Protect a narrow lead.";
        return "There is room to build an inning.";
    }
}
