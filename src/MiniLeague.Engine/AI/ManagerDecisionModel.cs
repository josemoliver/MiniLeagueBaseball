using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.AI;

/// <summary>Fast, deterministic one-play estimates for the computer manager; never rolls or changes a game.</summary>
internal static class ManagerDecisionModel
{
    private const int First = 1, Second = 2, Third = 4;

    // WHY: The same probability builder resolves the real at-bat. Comparing its outcomes keeps a choice tied to
    // this batter, this pitcher and his fatigue, this park, and the current defensive alignment.
    // NOTE: the statistics are requested by the opponent's hand, but the team data has no splits (TeamLoader copies the
    // overall line to both hands), so a same-hand and an opposite-hand matchup currently value the same.
    internal static double AtBat(GameState state, Player batter, Player pitcher, double fatigue,
        PitchType pitch, DefensiveAlignment alignment, OffensiveStrategy strategy = OffensiveStrategy.SwingAway)
    {
        var weights = ProbabilityTable.Build(batter.GetBattingStats(pitcher.ThrowHand),
            pitcher.GetPitchingStats(batter.BatHand), state.Stadium, batter.BatsAgainst(pitcher.ThrowHand),
            pitch, alignment, fatigue, DefenseModel.RatingsFor(state));
        if (strategy == OffensiveStrategy.HitAndRun)
            weights = weights with { Strikeout = weights.Strikeout * 0.75, Single = weights.Single * 1.08 };
        return FromWeights(state, weights, strategy, alignment);
    }

    private static double FromWeights(GameState state, OutcomeWeights w, OffensiveStrategy strategy,
        DefensiveAlignment alignment)
    {
        double total = w.HomeRun + w.Triple + w.Double + w.Single + w.Walk + w.HitByPitch
            + w.Strikeout + w.GroundOut + w.FlyOut;
        if (total <= 0) return StateValue(state, WinProbability.BaseState(state), state.Outs);

        int bases = WinProbability.BaseState(state), outs = state.Outs;
        double hit = w.HomeRun * HitValue(state, bases, outs, OutcomeType.HomeRun, strategy)
            + w.Triple * HitValue(state, bases, outs, OutcomeType.Triple, strategy)
            + w.Double * HitValue(state, bases, outs, OutcomeType.Double, strategy)
            + w.Single * HitValue(state, bases, outs, OutcomeType.Single, strategy);
        double walk = WalkValue(state, bases, outs);
        double strikeout = StateValue(state, bases, outs + 1);
        double ground = GroundValue(state, bases, outs, strategy == OffensiveStrategy.HitAndRun, alignment);
        double fly = FlyValue(state, bases, outs);

        // WHY: A hit and run is a real gamble in this engine. The runner may steal after a strikeout, or be
        // doubled off on a line drive. Include both rather than valuing only the contact boost.
        if (strategy == OffensiveStrategy.HitAndRun && state.RunnerOnFirst is { } runner)
        {
            double steal = StealChance(state, runner.Player, pitchout: false);
            strikeout = steal * StateValue(state, (bases & ~First) | Second, outs + 1)
                + (1 - steal) * StateValue(state, bases & ~First, outs + 2);
            double doubledOff = 0.35 * BuntModel.DoubledOffChance(runner.Player);
            fly = (1 - doubledOff) * fly + doubledOff * StateValue(state, bases & ~First, outs + 2);
        }
        return (hit + (w.Walk + w.HitByPitch) * walk + w.Strikeout * strikeout
            + w.GroundOut * ground + w.FlyOut * fly) / total;
    }

    // WHY: These transitions mirror the resolver's base-running rules in expectation. They do not need to predict
    // one random roll; they only need to compare two management calls without consuming the game's random stream.
    private static double HitValue(GameState s, int bases, int outs, OutcomeType hit, OffensiveStrategy strategy)
    {
        int runners = Count(bases);
        if (hit == OutcomeType.HomeRun) return StateValue(s, 0, outs, runners + 1);
        if (hit == OutcomeType.Triple) return StateValue(s, Third, outs, runners);
        int arm = (int)Math.Round((DefenseModel.RatingsFor(s).OutfieldArm - 3) * 6);
        if (hit == OutcomeType.Double)
        {
            int runs = Count(bases & (Second | Third));
            if ((bases & First) == 0) return StateValue(s, Second, outs, runs);
            double score = strategy == OffensiveStrategy.SwingAgressive
                ? SpeedChance(s.RunnerOnFirst!.Player, 60 - arm)
                : strategy == OffensiveStrategy.HitAndRun
                    ? SpeedChance(s.RunnerOnFirst!.Player, 72 - arm) : 0;
            return score * StateValue(s, Second, outs, runs + 1)
                + (1 - score) * StateValue(s, Second | Third, outs, runs);
        }

        int fromThird = (bases & Third) != 0 ? 1 : 0;
        double secondScores = (bases & Second) != 0
            ? SpeedChance(s.RunnerOnSecond!.Player,
                (strategy == OffensiveStrategy.SwingAgressive ? 55 : 30) - arm) : 0;
        double value = 0;
        foreach (bool scores in new[] { false, true })
        {
            double chance = (bases & Second) == 0 ? (scores ? 0 : 1)
                : scores ? secondScores : 1 - secondScores;
            if (chance == 0) continue;
            int after = First | ((bases & Second) != 0 && !scores ? Third : 0);
            int runs = fromThird + (scores ? 1 : 0);
            if ((bases & First) == 0) { value += chance * StateValue(s, after, outs, runs); continue; }
            double extra = (after & Third) == 0 && strategy is (OffensiveStrategy.SwingAgressive or OffensiveStrategy.HitAndRun)
                ? SpeedChance(s.RunnerOnFirst!.Player,
                    (strategy == OffensiveStrategy.HitAndRun ? 65 : 50) - arm) : 0;
            value += chance * (extra * StateValue(s, after | Third, outs, runs)
                + (1 - extra) * StateValue(s, after | Second, outs, runs));
        }
        return value;
    }

    internal static double WalkValue(GameState s, int bases, int outs)
    {
        int after = bases | First;
        if ((bases & First) != 0) after |= Second;
        if ((bases & (First | Second)) == (First | Second)) after |= Third;
        return StateValue(s, after, outs, bases == 7 ? 1 : 0);
    }

    private static double GroundValue(GameState s, int bases, int outs, bool runnerGoing,
        DefensiveAlignment alignment)
    {
        int after = ((bases & First) != 0 ? Second : 0) | ((bases & Second) != 0 ? Third : 0);
        double ordinary = StateValue(s, after, outs + 1, (bases & Third) != 0 && outs < 2 ? 1 : 0);
        if ((bases & First) == 0 || outs >= 2 || alignment == DefensiveAlignment.InfieldIn) return ordinary;
        double dp = runnerGoing ? 0.12 : 0.45;
        int dpAfter = (bases & Second) != 0 ? Third : 0;
        double doublePlay = StateValue(s, dpAfter, outs + 2, (bases & Third) != 0 && outs == 0 ? 1 : 0);
        return dp * doublePlay + (1 - dp) * ordinary;
    }

    private static double FlyValue(GameState s, int bases, int outs)
    {
        double ordinary = StateValue(s, bases, outs + 1);
        if ((bases & Third) == 0 || outs >= 2) return ordinary;
        int arm = (int)Math.Round((DefenseModel.RatingsFor(s).OutfieldArm - 3) * 6);
        double score = SpeedChance(s.RunnerOnThird!.Player, 65 - arm);
        return score * StateValue(s, bases & ~Third, outs + 1, 1) + (1 - score) * ordinary;
    }

    internal static double Steal(GameState s, Player runner)
    {
        int bases = WinProbability.BaseState(s);
        double p = StealChance(s, runner, pitchout: false);
        return p * StateValue(s, (bases & ~First) | Second, s.Outs)
            + (1 - p) * StateValue(s, bases & ~First, s.Outs + 1);
    }

    /// <summary>Expected-run cost to the fielding side of a pitchout the runner does not go on (a wasted ball).</summary>
    internal const double PitchoutBallCost = 0.05;

    /// <summary>
    /// How likely the batting side is to send this runner. Uses the same bar the computer offense applies before it
    /// calls a steal, so a pitchout is only considered against a runner the offense would really send.
    /// </summary>
    internal static double RunnerGoChance(GameState s, Player runner)
    {
        double staying = StateValue(s, WinProbability.BaseState(s), s.Outs);
        return Steal(s, runner) > staying + 0.035 ? 0.5 : 0.05;
    }

    internal static double StealChance(GameState s, Player runner, bool pitchout)
    {
        var catcher = s.DefenderAt(Position.C);
        var stats = s.CurrentPitcher.Player.GetPitchingStats(s.CurrentBatter.BatHand);
        int attempts = (runner.Overall?.StolenBases ?? 0) + (runner.Overall?.CaughtStealing ?? 0);
        double record = attempts > 0 ? (double)runner.Overall!.StolenBases / attempts : 0.65;
        return Math.Clamp(record * (6 - stats.HoldRunners) / 5.0
            * (6 - (catcher?.ArmAt(Position.C) ?? 3)) / 5.0 * runner.SpeedRating / 3.0
            * (pitchout ? 0.15 : 1), 0.05, 0.95);
    }

    internal static double Bunt(GameState s, Player batter, DefensiveAlignment alignment)
    {
        int bases = WinProbability.BaseState(s), outs = s.Outs;
        if (bases == 0 || outs >= 2) return double.NegativeInfinity;
        int? from = BuntModel.FieldersChoiceFrom((bases & First) != 0, (bases & Second) != 0, (bases & Third) != 0);
        var target = from == 2 ? s.RunnerOnSecond : from == 1 ? s.RunnerOnFirst : null;
        var odds = BuntModel.Odds(batter, alignment, sacrifice: true, target?.Player);
        double error = 0.03; // NOTE: bunt errors vary by fielder; this is the small average before a fielder is picked.
        double ordinary = Math.Max(0, 1 - error - odds.PopUp - odds.Single - odds.FieldersChoice);
        int advanced = ((bases & First) != 0 ? Second : 0) | ((bases & Second) != 0 ? Third : 0);
        int runs = (bases & Third) != 0 ? 1 : 0;
        double value = error * StateValue(s, First | advanced, outs, runs)
            + odds.PopUp * StateValue(s, bases, outs + 1)
            + odds.Single * StateValue(s, First | advanced, outs, runs)
            + ordinary * StateValue(s, advanced, outs + 1, runs);
        if (from == 1)
            value += odds.FieldersChoice * StateValue(s, First | (bases & Third), outs + 1);
        else if (from == 2)
            value += odds.FieldersChoice * StateValue(s,
                First | ((bases & First) != 0 ? Second : 0), outs + 1,
                (bases & (First | Third)) == (First | Third) ? 1 : 0);
        return value;
    }

    internal static double StateValue(GameState s, int bases, int outs, int runs = 0)
    {
        double expected = runs + (outs >= 3 ? 0 : WinProbability.ExpectedRuns(bases, outs));
        int margin = s.IsTopOfInning ? s.VisitorTotalScore - s.HomeTotalScore
            : s.HomeTotalScore - s.VisitorTotalScore;
        // WHY: A single late run has more value than piling on when tied or one behind. Mixing the chance of at
        // least one run with run expectancy makes the manager protect outs without using the costly full-game
        // win-probability model on every candidate at every pitch.
        if (s.CurrentInning >= 8 && margin is (0 or -1))
            return 0.35 * expected + 0.65 * (1 - Math.Exp(-expected));
        return expected;
    }

    private static double SpeedChance(Player runner, int baseChance) =>
        Math.Clamp((baseChance + (runner.SpeedRating - 3) * 8) / 100.0, 0, 1);

    private static int Count(int bases) =>
        ((bases & First) != 0 ? 1 : 0) + ((bases & Second) != 0 ? 1 : 0) + ((bases & Third) != 0 ? 1 : 0);
}
