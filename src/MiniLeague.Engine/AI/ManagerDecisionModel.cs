using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.AI;

/// <summary>Fast, deterministic one-play estimates for the computer manager; never rolls or changes a game.</summary>
internal static class ManagerDecisionModel
{
    private const int First = 1, Second = 2, Third = 4;

    // ── One decision's worth of work, done once ──────────────────────────────

    // WHY: a decision values many calls against the same situation, and each valuation used to rebuild the same things: the
    // fielders' ratings (up to four times), what a run is worth right now, and the same batter-and-pitcher valuation again
    // (the best pitch was found, then valued a second time, and every reliever's value was recomputed for each candidate).
    // None of it changes while the manager thinks, so it is kept for the length of one decision. The situation is the key:
    // the scope is tied to one game state and thrown away when the decision ends.
    private sealed class Cache(GameState state)
    {
        public readonly GameState State = state;
        public int Depth;
        public DefenseRatings? Ratings;
        public double? PerRun;
        public readonly Dictionary<(int, int, double, PitchType, DefensiveAlignment, OffensiveStrategy, bool, int), double> AtBat = [];
        public readonly Dictionary<(string, int, double), double> Memo = [];
    }

    [ThreadStatic] private static Cache? _cache;

    private static Cache? CacheFor(GameState s) => _cache is { } c && ReferenceEquals(c.State, s) ? c : null;

    /// <summary>Starts a decision about this state; dispose it when the decision is made. Nested decisions share the one scope.</summary>
    internal static IDisposable Decision(GameState state)
    {
        if (CacheFor(state) is { } open) { open.Depth++; return new Scope(open); }
        var cache = new Cache(state) { Depth = 1 };
        _cache = cache;
        return new Scope(cache);
    }

    private sealed class Scope(Cache cache) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--cache.Depth == 0 && ReferenceEquals(_cache, cache)) _cache = null;
        }
    }

    /// <summary>The fielders' ratings for this state, worked out once per decision.</summary>
    internal static DefenseRatings Ratings(GameState s)
    {
        if (CacheFor(s) is not { } c) return DefenseModel.RatingsFor(s);
        return c.Ratings ??= DefenseModel.RatingsFor(s);
    }

    /// <summary>A value worked out at most once per decision for this key (a pitcher's value over the next hitters, say).</summary>
    internal static double Memo(GameState s, string kind, int id, double detail, Func<double> compute)
    {
        if (CacheFor(s) is not { } c) return compute();
        var key = (kind, id, detail);
        if (!c.Memo.TryGetValue(key, out double value)) c.Memo[key] = value = compute();
        return value;
    }

    // WHY: The same probability builder resolves the real at-bat. Comparing its outcomes keeps a choice tied to
    // this batter, this pitcher and his fatigue, the platoon matchup between them, this park, and the alignment.
    // `platoon: false` values the matchup as if the hitter and pitcher were of any hand, so a caller can measure how much of a
    // value the platoon effect is (and a manager who weighs it differently can scale it).
    internal static double AtBat(GameState state, Player batter, Player pitcher, double fatigue,
        PitchType pitch, DefensiveAlignment alignment, OffensiveStrategy strategy = OffensiveStrategy.SwingAway,
        bool platoon = true)
    {
        // NOTE: nothing is cached while a situation is imagined or a hitter is being looked ahead to: the same call then has a
        // different value from the real one. (The next hitter's own value, built from the pieces below, is kept by Memo.)
        var cache = _imagined ? null : CacheFor(state);
        var key = (batter.Id, pitcher.Id, fatigue, pitch, alignment, strategy, platoon, _lookaheadHitter);
        if (cache is not null && cache.AtBat.TryGetValue(key, out double known)) return known;

        var weights = ProbabilityTable.Build(batter.GetBattingStats(pitcher.ThrowHand),
            pitcher.GetPitchingStats(batter.BatHand), state.Stadium, batter.BatsAgainst(pitcher.ThrowHand),
            pitch, alignment, fatigue, Ratings(state), platoon ? pitcher.ThrowHand : null);
        if (strategy == OffensiveStrategy.HitAndRun)
            weights = RunningRules.ApplyHitAndRun(weights);
        double doublePlay = RunningRules.DoublePlayChanceFor(batter.SpeedRating,
            pitcher.GetPitchingStats(batter.BatHand).GroundBallRate, strategy == OffensiveStrategy.HitAndRun);
        double value = FromWeights(state, weights, strategy, alignment, doublePlay);
        if (cache is not null) cache.AtBat[key] = value;
        return value;
    }

    private static double FromWeights(GameState state, OutcomeWeights w, OffensiveStrategy strategy,
        DefensiveAlignment alignment, double doublePlayChance)
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
        double ground = GroundValue(state, bases, outs, doublePlayChance, alignment);
        double fly = FlyValue(state, bases, outs);

        // WHY: A hit and run is a real gamble in this engine. The runner may steal after a strikeout, or be
        // doubled off on a line drive. Include both rather than valuing only the contact boost.
        if (strategy == OffensiveStrategy.HitAndRun && state.RunnerOnFirst is { } runner)
        {
            double steal = StealChance(state, runner.Player, pitchout: false);
            strikeout = steal * StateValue(state, (bases & ~First) | Second, outs + 1)
                + (1 - steal) * StateValue(state, bases & ~First, outs + 2);
            double doubledOff = RunningRules.HitAndRunLineOutShare * BuntModel.DoubledOffChance(runner.Player);
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
        int arm = RunningRules.ArmPenalty(Ratings(s).OutfieldArm);
        bool aggressive = strategy == OffensiveStrategy.SwingAgressive;
        // WHY: an aggressive send that fails is usually thrown out (BaseRunningEngine.SentRunnerThrownOut), and the
        // model has to charge for it or aggressive running looks free again.
        double sentOut = BaseRunningEngine.SentRunnerThrownOut;
        if (hit == OutcomeType.Double)
        {
            int runs = Count(bases & (Second | Third));
            if ((bases & First) == 0) return StateValue(s, Second, outs, runs);
            double score = aggressive
                ? SpeedChance(s.RunnerOnFirst!.Player, 60 - arm)
                : strategy == OffensiveStrategy.HitAndRun
                    ? SpeedChance(s.RunnerOnFirst!.Player, 72 - arm) : 0;
            double outAtHome = aggressive ? (1 - score) * sentOut : 0;
            return score * StateValue(s, Second, outs, runs + 1)
                + outAtHome * StateValue(s, Second, outs + 1, runs)
                + (1 - score - outAtHome) * StateValue(s, Second | Third, outs, runs);
        }

        int fromThird = (bases & Third) != 0 ? 1 : 0;
        // The runner from second: scores, is held at third, or (sent hard) is thrown out at the plate.
        var fromSecond = new List<(double Chance, bool Scores, bool AtThird, int Out)>();
        if ((bases & Second) == 0) fromSecond.Add((1, false, false, 0));
        else
        {
            double p = SpeedChance(s.RunnerOnSecond!.Player, (aggressive ? 55 : 30) - arm);
            double outAtHome = aggressive ? (1 - p) * sentOut : 0;
            fromSecond.Add((p, true, false, 0));
            fromSecond.Add((1 - p - outAtHome, false, true, 0));
            if (outAtHome > 0) fromSecond.Add((outAtHome, false, false, 1));
        }

        double value = 0;
        foreach (var (chance, scores, atThird, outMade) in fromSecond)
        {
            if (chance <= 0) continue;
            int after = First | (atThird ? Third : 0);
            int runs = fromThird + (scores ? 1 : 0);
            int outsAfter = outs + outMade;
            if ((bases & First) == 0) { value += chance * StateValue(s, after, outsAfter, runs); continue; }

            // The runner from first tries for third only if third is open and nobody was thrown out ahead of him.
            bool canTry = !atThird && outMade == 0;
            double extra = canTry && strategy is (OffensiveStrategy.SwingAgressive or OffensiveStrategy.HitAndRun)
                ? SpeedChance(s.RunnerOnFirst!.Player,
                    (strategy == OffensiveStrategy.HitAndRun ? 65 : 50) - arm) : 0;
            double outAtThird = canTry && aggressive ? (1 - extra) * sentOut : 0;
            value += chance * (extra * StateValue(s, after | Third, outsAfter, runs)
                + outAtThird * StateValue(s, after, outsAfter + 1, runs)
                + (1 - extra - outAtThird) * StateValue(s, after | Second, outsAfter, runs));
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

    private static double GroundValue(GameState s, int bases, int outs, double dp,
        DefensiveAlignment alignment)
    {
        int after = ((bases & First) != 0 ? Second : 0) | ((bases & Second) != 0 ? Third : 0);
        double ordinary = StateValue(s, after, outs + 1, (bases & Third) != 0 && outs < 2 ? 1 : 0);
        // WHY: with the infield in an unforced runner on third usually holds (BaseRunningEngine.GroundOut). That is
        // the reason to play in, so the model has to see it.
        bool forced = (bases & (First | Second)) == (First | Second);
        if (alignment == DefensiveAlignment.InfieldIn && (bases & Third) != 0 && outs < 2 && !forced)
        {
            double scores = BaseRunningEngine.InfieldInScoreChance(s.RunnerOnThird!.Player);
            int held = Third | ((bases & First) != 0 ? Second : bases & Second);
            ordinary = scores * ordinary + (1 - scores) * StateValue(s, held, outs + 1);
        }
        if ((bases & First) == 0 || outs >= 2 || alignment == DefensiveAlignment.InfieldIn) return ordinary;
        int dpAfter = (bases & Second) != 0 ? Third : 0;
        double doublePlay = StateValue(s, dpAfter, outs + 2, (bases & Third) != 0 && outs == 0 ? 1 : 0);
        return dp * doublePlay + (1 - dp) * ordinary;
    }

    private static double FlyValue(GameState s, int bases, int outs)
    {
        double ordinary = StateValue(s, bases, outs + 1);
        if ((bases & Third) == 0 || outs >= 2) return ordinary;
        int arm = RunningRules.ArmPenalty(Ratings(s).OutfieldArm);
        // NOTE: only an outfield fly can score him (AtBatResolver), so the tag-up chance is scaled by how many flies go there.
        double score = SpeedChance(s.RunnerOnThird!.Player, RunningRules.TagUpFromThird - arm) * DefenseModel.OutfieldFlyShare;
        return score * StateValue(s, bases & ~Third, outs + 1, 1) + (1 - score) * ordinary;
    }

    /// <summary>A steal of third by the runner on second (first and third empty).</summary>
    internal static double StealThird(GameState s)
    {
        int bases = WinProbability.BaseState(s);
        double p = Math.Clamp(StealChance(s, s.RunnerOnSecond!.Player, pitchout: false) * RunningRules.StealOfThirdFactor, 0.05, 0.95);
        return p * StateValue(s, (bases & ~Second) | Third, s.Outs)
            + (1 - p) * StateValue(s, bases & ~Second, s.Outs + 1);
    }

    /// <summary>A double steal with runners on first and second (third empty): the catcher picks a base to throw to.</summary>
    internal static double DoubleSteal(GameState s)
    {
        int outs = s.Outs;
        double toThird = Math.Clamp(StealChance(s, s.RunnerOnSecond!.Player, pitchout: false) * RunningRules.StealOfThirdFactor, 0.05, 0.95);
        double toSecond = StealChance(s, s.RunnerOnFirst!.Player, pitchout: false);
        double both = StateValue(s, Second | Third, outs);
        double leadOut = StateValue(s, Second, outs + 1);      // out at third; the trailing runner is on second
        double trailingOut = StateValue(s, Third, outs + 1);   // out at second; the lead runner is on third
        double throwThird = RunningRules.DoubleStealThrowToThird;
        return throwThird * (toThird * both + (1 - toThird) * leadOut)
            + (1 - throwThird) * (toSecond * both + (1 - toSecond) * trailingOut);
    }

    /// <summary>
    /// A squeeze with a runner on third and fewer than two out, valued like <see cref="Bunt"/>: the ways it can end, in the
    /// state each leaves. A suicide squeeze scores the run on a bunt that is down, loses the runner (doubled off) when it is
    /// popped up, and can be read, with the runner out at the plate. A safety squeeze scores him only part of the time but
    /// never loses him.
    /// </summary>
    internal static double Squeeze(GameState s, Player batter, DefensiveAlignment alignment, SqueezeKind kind)
    {
        int bases = WinProbability.BaseState(s), outs = s.Outs;
        if ((bases & Third) == 0 || outs >= 2) return double.NegativeInfinity;
        var runner = s.RunnerOnThird!.Player;
        var odds = BuntModel.SqueezeOdds(batter, alignment, kind, runner);
        const double error = 0.03;   // as for any bunt: a small average chance the fielder boots it
        bool suicide = kind == SqueezeKind.Suicide;

        int movedUp = ((bases & First) != 0 ? Second : 0) | ((bases & Second) != 0 ? Third : 0);   // the others, a base each
        double ordinary = Math.Max(0, 1 - error - odds.PopUp - odds.Single - (suicide ? odds.FieldersChoice : 0));

        double value = error * StateValue(s, First | movedUp, outs, 1)
            + odds.Single * StateValue(s, First | movedUp, outs, 1)
            + odds.PopUp * (suicide ? StateValue(s, bases & ~Third, outs + 2) : StateValue(s, bases, outs + 1));
        if (suicide)
            value += odds.FieldersChoice * StateValue(s, First | movedUp, outs + 1)   // runner out at the plate, batter safe
                   + ordinary * StateValue(s, movedUp, outs + 1, 1);
        else
        {
            double scores = BuntModel.SafetyScoreChance(batter, runner, alignment);
            int held = Third | ((bases & (First | Second)) != 0 ? Second : 0) | ((bases & (First | Second)) == (First | Second) ? First : 0);
            value += ordinary * (scores * StateValue(s, movedUp, outs + 1, 1) + (1 - scores) * StateValue(s, held, outs + 1));
        }
        return value;
    }

    internal static double Steal(GameState s, Player runner)
    {
        int bases = WinProbability.BaseState(s);
        double p = StealChance(s, runner, pitchout: false);
        return p * StateValue(s, (bases & ~First) | Second, s.Outs)
            + (1 - p) * StateValue(s, bases & ~First, s.Outs + 1);
    }

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
        return BaseRunningEngine.StealSuccessChance(runner, catcher?.ArmAt(Position.C) ?? 3, stats.HoldRunners, pitchout);
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

    /// <summary>
    /// The value to the batting side of the situation after a play: bases, outs, and runs scored on it.
    /// WHY: what a manager wants is to win, so this is the batting side's chance of winning from that situation,
    /// divided by what one more run is worth right now. Dividing keeps the value in runs, so every margin in
    /// <see cref="ManagerTuning"/> keeps its meaning, while the shape follows the game: early on a run is a run, but
    /// late and close the first run is worth far more than the second, a lead protects itself, and a walk-off ends it.
    /// In a game that is already decided a run changes almost nothing, so plain run expectancy takes over.
    /// </summary>
    internal static double StateValue(GameState s, int bases, int outs, int runs = 0)
    {
        runs += _carry;
        // While looking ahead, a situation with outs left is worth what the next hitter makes of it (see Lookahead).
        if (_continuation is { } next && outs < 3) return next(bases, outs, runs);
        return PlainValue(s, bases, outs, runs);
    }

    // The value of a situation with the rest of the inning played by average hitters.
    private static double PlainValue(GameState s, int bases, int outs, int runs)
    {
        double perRun = PerRun(s);
        if (perRun < 0.01)
            return runs + (outs >= 3 ? 0 : WinProbability.ExpectedRuns(bases, outs));
        return BattingWins(s, bases, outs, runs) / perRun;
    }

    // ── Looking a hitter further ahead ───────────────────────────────────────
    //
    // WHY: a run-expectancy table values "a runner on second with one out" for an average hitter at the plate. A bunt or an
    // intentional walk is a choice about who bats next in that situation, and the table cannot see that: a bunt that moves
    // the runner is worth more with a good hitter on deck, and a walk is a bargain when the next man is a weak one. So the
    // call is valued by the situation it leaves, and what the actual next hitter makes of it (a plate appearance valued the
    // usual way). The "next hitter" is evaluated by briefly setting the bases and outs on the state and putting them back.

    [ThreadStatic] private static int _carry;                      // runs already scored by the call being valued
    [ThreadStatic] private static Func<int, int, int, double>? _continuation;
    [ThreadStatic] private static double? _fixedPerRun;            // what a run is worth in the real situation, not the one imagined
    [ThreadStatic] private static int _lookaheadHitter;            // 0 when not looking ahead, else the hitter being looked ahead to
    [ThreadStatic] private static bool _imagined;                  // the state is temporarily not the real one: nothing may be cached

    private static readonly BaseRunner AverageRunner = new() { Player = new Player { Name = "a runner", SpeedRating = 3 } };

    /// <summary>
    /// While the scope is open, every situation a call leaves is valued as the next hitter's plate appearance from it (against
    /// this pitcher, who throws his best pitch to him). Dispose it to go back to run expectancy.
    /// </summary>
    internal static IDisposable Lookahead(GameState s, Player nextHitter, Player pitcher, double fatigue)
    {
        var saved = (_continuation, _fixedPerRun, _lookaheadHitter);
        _fixedPerRun = PerRun(s);   // worked out now, while the situation is the real one
        _lookaheadHitter = nextHitter.Id;
        _continuation = (bases, outs, runs) => Memo(s, $"look{nextHitter.Id}/{pitcher.Id}", bases * 10 + outs, runs + fatigue / 100.0,
            () => HitterFrom(s, nextHitter, pitcher, fatigue, bases, outs, runs));
        return new LookaheadScope(saved);
    }

    private sealed class LookaheadScope((Func<int, int, int, double>?, double?, int) saved) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            (_continuation, _fixedPerRun, _lookaheadHitter) = saved;
        }
    }

    // The next hitter's plate appearance from this situation, with `runs` already scored, valued with the pitcher's best pitch.
    private static double HitterFrom(GameState s, Player hitter, Player pitcher, double fatigue, int bases, int outs, int runs)
    {
        var continuation = _continuation;
        _continuation = null;   // one hitter ahead is the horizon: after him, average hitters
        try
        {
            return OnBases(s, bases, outs, runs, () => BestPitchValue(s, hitter, pitcher, fatigue));
        }
        finally { _continuation = continuation; }
    }

    // The value to the offense of a hitter's plate appearance against the pitcher's best pitch (the one that costs the offense most).
    private static double BestPitchValue(GameState s, Player hitter, Player pitcher, double fatigue)
    {
        double best = double.PositiveInfinity;
        foreach (var pitch in new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup })
            best = Math.Min(best, AtBat(s, hitter, pitcher, fatigue, pitch, DefensiveAlignment.Standard));
        return best;
    }

    // Runs `compute` with these bases and outs on the state, and `carried` runs already scored, then puts everything back.
    // A runner already on a base stays; one who is not there yet (the batter who reached, say) is an average runner.
    private static double OnBases(GameState s, int bases, int outs, int carried, Func<double> compute)
    {
        var (first, second, third, was, carry, imagined) = (s.RunnerOnFirst, s.RunnerOnSecond, s.RunnerOnThird, s.Outs, _carry, _imagined);
        s.RunnerOnFirst = (bases & First) != 0 ? first ?? AverageRunner : null;
        s.RunnerOnSecond = (bases & Second) != 0 ? second ?? AverageRunner : null;
        s.RunnerOnThird = (bases & Third) != 0 ? third ?? AverageRunner : null;
        s.Outs = outs;
        _carry = carried;
        _imagined = true;
        try { return compute(); }
        finally
        {
            (s.RunnerOnFirst, s.RunnerOnSecond, s.RunnerOnThird, s.Outs, _carry, _imagined) = (first, second, third, was, carry, imagined);
        }
    }

    /// <summary>
    /// What an intentional walk of this batter is worth: the situation it leaves (the batter on first, forced runners moved up),
    /// valued as the on-deck hitter's plate appearance from it. Compare with pitching to the batter.
    /// </summary>
    internal static double WalkThenNextHitter(GameState s, Player onDeck, Player pitcher, double fatigue)
    {
        int bases = WinProbability.BaseState(s);
        int after = bases | First;
        if ((bases & First) != 0) after |= Second;
        if ((bases & (First | Second)) == (First | Second)) after |= Third;
        double fixedPerRun = PerRun(s);
        var saved = (_continuation, _fixedPerRun);
        _continuation = null;
        _fixedPerRun = fixedPerRun;
        try { return OnBases(s, after, s.Outs, bases == 7 ? 1 : 0, () => BestPitchValue(s, onDeck, pitcher, fatigue)); }
        finally { (_continuation, _fixedPerRun) = saved; }
    }

    // What one more run is worth to the batting side right now, in chance of winning.
    internal static double PerRun(GameState s)
    {
        if (_fixedPerRun is double fixedValue) return fixedValue;
        var cache = CacheFor(s);
        if (cache?.PerRun is double known) return known;
        int bases = WinProbability.BaseState(s);
        double value = BattingWins(s, bases, s.Outs, 1) - BattingWins(s, bases, s.Outs, 0);
        if (cache is not null) cache.PerRun = value;
        return value;
    }

    // What a run is worth in the first inning of a tied game: the yardstick for "how important is this moment".
    private static readonly double BaselinePerRun =
        WinProbability.HomeWinsCached(1, true, 0, 0, 1) - WinProbability.HomeWinsCached(1, true, 0, 0, 0);

    /// <summary>
    /// How much more this moment matters than the first inning of a tied game: 1 is that opening moment, 3 a tight game
    /// in the ninth, near 0 a game that is already decided.
    /// </summary>
    internal static double Leverage(GameState s) => Math.Clamp(PerRun(s) / BaselinePerRun, 0.05, 6);

    // The batting side's chance of winning with these bases and outs after `runs` more score in this half-inning.
    private static double BattingWins(GameState s, int bases, int outs, int runs)
    {
        bool top = s.IsTopOfInning;
        int inning = s.CurrentInning;
        int homeMargin = s.HomeTotalScore - s.VisitorTotalScore + (top ? -runs : runs);
        double home;
        if (!top && inning >= 9 && homeMargin > 0)
            home = 1;                                   // a walk-off: the game is over the moment the run scores
        else if (outs < 3)
            home = WinProbability.HomeWinsCached(inning, top, outs, bases, homeMargin);
        else if (top)
            home = inning >= 9 && homeMargin > 0 ? 1    // home leads after the top of the ninth: no need to bat
                : WinProbability.HomeWinsCached(inning, false, 0, 0, homeMargin);
        else
            home = inning >= 9 && homeMargin < 0 ? 0    // the visitors lead when the home ninth ends
                : WinProbability.HomeWinsCached(inning + 1, true, 0, 0, homeMargin);
        return top ? 1 - home : home;
    }

    private static double SpeedChance(Player runner, int baseChance) => RunningRules.SpeedChance(runner, baseChance);

    private static int Count(int bases) =>
        ((bases & First) != 0 ? 1 : 0) + ((bases & Second) != 0 ? 1 : 0) + ((bases & Third) != 0 ? 1 : 0);
}
