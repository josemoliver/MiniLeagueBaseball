using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;
using PitchingMove = MiniLeague.Engine.AI.ComputerManager.PitchingMove;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// The philosophy dials of <see cref="ManagerTuning"/>: appetite for risk by the score, leverage sensitivity, how
/// unpredictable the fielding manager is, a starter's pitch limit, when the closer comes in, platoon emphasis and late
/// defensive replacements. Each test builds one situation and changes only the dial.
/// </summary>
public class ManagerDialsTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    // The visitors bat in the top of the first; the home team fields.
    private static GameState Game(int seed = 1)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), ManagerControl.Computer, ManagerControl.Computer,
            options: GameOptions.None);
        return game.State!;
    }

    private static ComputerManager Manager(ManagerTuning tuning, int seed = 1) => new(new Random(seed), tuning);

    // ── Leverage ─────────────────────────────────────────────────────────────

    [Fact]
    public void ATightLateGameMattersMoreThanTheOpeningInning_AndABlowoutAlmostNothing()
    {
        double At(int inning, bool top, int visitorRuns, int homeRuns)
        {
            var s = Game();
            s.CurrentInning = inning;
            s.IsTopOfInning = top;
            s.VisitorScoreByInning[0] = visitorRuns;
            s.HomeScoreByInning[0] = homeRuns;
            return ManagerDecisionModel.Leverage(s);
        }

        double opening = At(1, true, 0, 0);
        double tightNinth = At(9, false, 3, 3);
        double blowout = At(9, true, 0, 12);

        Assert.InRange(opening, 0.9, 1.1);   // the yardstick itself
        Assert.True(tightNinth > opening * 2, $"{tightNinth:0.00} vs {opening:0.00}");
        Assert.True(blowout < 0.3, $"{blowout:0.00}");
    }

    [Fact]
    public void LeverageScalesAnEdgeTheRightWay()
    {
        Assert.Equal(1.0, ComputerManager.LeverageScale(3.0, 0));          // a manager who ignores it
        Assert.True(ComputerManager.LeverageScale(3.0, 0.5) < 1.0);        // important moment, sensitive: acts on less
        Assert.True(ComputerManager.LeverageScale(0.3, 0.5) > 1.0);        // unimportant: wants more
        Assert.True(ComputerManager.LeverageScale(3.0, -0.5) > 1.0);       // stubborn when it matters

        // A factor below 1 makes any margin easier to clear, whichever way it points.
        Assert.True(ComputerManager.Scale(0.04, 0.5) < 0.04);
        Assert.True(ComputerManager.Scale(-0.04, 0.5) < -0.04);
        Assert.Equal(0.04, ComputerManager.Scale(0.04, 1.0));
    }

    [Fact]
    public void ALeverageSensitiveManagerChangesPitchersOnASmallerEdgeInATightNinth()
    {
        // The same warm reliever, stepped from excellent to awful; two managers with the same bar, one leverage-sensitive.
        bool Changes(double sensitivity, double relieverEra)
        {
            var state = Game();
            state.CurrentInning = 9;
            state.HomePitcher.PitchCount = 85;
            state.HomePitcher.BattersFaced = 24;
            var reliever = state.HomeBullpen.First(w => w.Player.Role != PitcherRole.Closer);
            foreach (var w in state.HomeBullpen) w.IsWarming = false;
            reliever.IsWarming = true;
            reliever.BattersFacedWarming = 5;
            // WHY: with league figures a pitcher is scaled by his hit and home-run rates, not his ERA, so give these two the
            // fixed-ERA scale; the ladder then steps quality by ERA alone.
            reliever.Player.PitchingOverall!.League = LeaguePitching.Default;
            state.HomePitcher.Player.PitchingOverall!.League = LeaguePitching.Default;
            reliever.Player.PitchingOverall!.ERA = relieverEra;
            state.HomePitcher.Player.PitchingOverall!.ERA = 4.5;   // a starter who can be beaten
            // Tied in the ninth, a close game: the Ok-condition bar applies.
            return Manager(new ManagerTuning { LeverageSensitivity = sensitivity, ReliefEdgeClose = 0.01, QuickHookPitchCount = 60 })
                .DecidePitching(state, visitor: false).ChangeToId == reliever.Player.Id;
        }

        // WHY: a fine ladder, because the two bars can sit within a quarter of an ERA point of each other
        var ladder = Enumerable.Range(0, 200).Select(i => 0.5 + i * 0.05).ToList();
        int plain = ladder.Count(era => Changes(0, era));
        int sensitive = ladder.Count(era => Changes(1.0, era));
        int stubborn = ladder.Count(era => Changes(-1.0, era));

        Assert.True(sensitive > plain, $"sensitive {sensitive} vs plain {plain} ERA steps");
        Assert.True(plain >= stubborn, $"plain {plain} vs stubborn {stubborn} ERA steps");
    }

    // ── Risk by the score ────────────────────────────────────────────────────

    [Fact]
    public void ARiskTakerSteals_WhenBehindLate_ButNotWhenTied()
    {
        var state = Game();
        state.CurrentInning = 8;
        var runner = state.LineupPlayers(true).Skip(4).First().Player;
        runner.SpeedRating = 4;
        runner.Overall = new BatterStats { AtBats = 300, Hits = 80, StolenBases = 22, CaughtStealing = 9 };
        state.RunnerOnFirst = new BaseRunner { Player = runner };

        // What a steal is worth depends on the score, so the bar is set for each situation: just above its worth, so only
        // an appetite for risk (or against it) can change the call.
        ManagerTuning BarJustAboveTheSteal() => new()
        {
            StealMargin = ManagerDecisionModel.Steal(state, runner)
                - ManagerDecisionModel.StateValue(state, WinProbability.BaseState(state), state.Outs) + 0.02,
        };
        bool Steals(ManagerTuning t) => Manager(t).MakeOffensiveDecision(state).Strategy == OffensiveStrategy.Steal;

        // Tied: the score gives no reason to gamble, whatever his appetite.
        var tied = BarJustAboveTheSteal();
        Assert.False(Steals(tied));
        Assert.False(Steals(tied with { SituationalRisk = 1.0 }));

        // Down a run in the eighth: the one who likes risk goes, the one who dislikes it does not.
        state.HomeScoreByInning[0] = 1;
        var behind = BarJustAboveTheSteal();
        Assert.False(Steals(behind));
        Assert.True(Steals(behind with { SituationalRisk = 1.0 }));
        Assert.False(Steals(behind with { SituationalRisk = -1.0 }));
    }

    // ── Unpredictability ─────────────────────────────────────────────────────

    [Fact]
    public void APitchMixSpreadsTheFieldingCallsOverSeveralPitches_AndNoneAlwaysTakesTheBest()
    {
        var state = Game();
        var batter = state.CurrentBatter;
        batter.PowerRating = 3;

        (double Top, int Distinct) Spread(ManagerTuning tuning)
        {
            var calls = Enumerable.Range(0, 300).Select(seed =>
            {
                var d = Manager(tuning, seed).MakeDefensiveDecision(state);
                return (d.PitchType, d.Alignment);
            }).GroupBy(c => c).Select(g => g.Count()).OrderByDescending(n => n).ToList();
            return (calls[0] / 300.0, calls.Count);
        }

        var plain = Spread(new ManagerTuning());
        var mixed = Spread(new ManagerTuning { PitchMix = 0.03 });

        Assert.True(plain.Top > 0.8, $"plain: the best call {plain.Top:P0} of the time");
        Assert.True(mixed.Top < 0.5, $"mixed: the best call {mixed.Top:P0} of the time");
        Assert.True(mixed.Distinct > plain.Distinct);
    }

    [Fact]
    public void ThePitchMixStillFavorsTheBetterOptions()
    {
        var state = Game();
        var batter = state.CurrentBatter;
        var pitcher = state.CurrentPitcher.Player;
        double fatigue = FatigueModel.GetModifier(state.CurrentPitcher);
        var values = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }
            .ToDictionary(p => p, p => ManagerDecisionModel.AtBat(state, batter, pitcher, fatigue, p, DefensiveAlignment.Standard));
        var best = values.MinBy(kv => kv.Value).Key;
        var worst = values.MaxBy(kv => kv.Value).Key;

        var counts = Enumerable.Range(0, 800)
            .Select(seed => Manager(new ManagerTuning { PitchMix = 0.01 }, seed).MakeDefensiveDecision(state))
            .Where(d => d.Alignment == DefensiveAlignment.Standard)
            .GroupBy(d => d.PitchType).ToDictionary(g => g.Key, g => g.Count());

        Assert.True(counts.GetValueOrDefault(best) > counts.GetValueOrDefault(worst),
            $"best {best} {counts.GetValueOrDefault(best)}, worst {worst} {counts.GetValueOrDefault(worst)}");
    }

    // ── A starter's pitch limit ──────────────────────────────────────────────

    [Fact]
    public void AStarterIsReplacedAtHisPitchLimit_EvenWhileHeIsPitchingWell()
    {
        PitchingMove Decide(ManagerTuning tuning, int pitches)
        {
            var state = Game();
            state.CurrentInning = 6;
            state.HomePitcher.PitchCount = pitches;
            state.HomePitcher.BattersFaced = pitches / 4;
            state.HomePitcher.Player.PitchingOverall!.ERA = 1.5;   // pitching beautifully
            var reliever = state.HomeBullpen.First(w => w.Player.Role != PitcherRole.Closer);
            foreach (var w in state.HomeBullpen) w.IsWarming = false;
            reliever.IsWarming = true;
            reliever.BattersFacedWarming = 5;
            reliever.Player.PitchingOverall!.ERA = 5.5;
            return Manager(tuning).DecidePitching(state, visitor: false);
        }

        var limited = new ManagerTuning { StarterPitchLimit = 90 };

        Assert.NotNull(Decide(limited, 95).ChangeToId);
        Assert.Contains("limit", Decide(limited, 95).Why);
        Assert.Null(Decide(limited, 70).ChangeToId);               // under the limit: he stays
        Assert.Null(Decide(new ManagerTuning(), 95).ChangeToId);   // no limit: he stays
    }

    [Fact]
    public void ARelieverStartsWarmingAsTheStarterNearsHisPitchLimit()
    {
        var state = Game();
        state.CurrentInning = 5;
        state.HomePitcher.PitchCount = 80;
        state.HomePitcher.BattersFaced = 20;
        state.HomePitcher.Player.PitchingOverall!.ERA = 1.5;
        foreach (var w in state.HomeBullpen) w.IsWarming = false;

        Assert.NotNull(Manager(new ManagerTuning { StarterPitchLimit = 90 }).DecidePitching(state, false).StartWarmingId);
        Assert.Null(Manager(new ManagerTuning()).DecidePitching(state, false).StartWarmingId);
    }

    // ── The closer ───────────────────────────────────────────────────────────

    [Fact]
    public void AFiremanBringsTheCloserInFromTheEighth_AndTheClassicCloserWaitsForTheNinth()
    {
        PitchingMove Decide(ManagerTuning tuning, int inning)
        {
            var state = Game();
            state.CurrentInning = inning;
            state.HomeScoreByInning[0] = 2;   // the home team leads by two
            state.HomePitcher.PitchCount = 60;
            state.HomePitcher.BattersFaced = 15;
            foreach (var w in state.HomeBullpen) w.IsWarming = false;
            var closer = state.HomeBullpen.First(w => w.Player.Role == PitcherRole.Closer);
            closer.IsWarming = true;
            closer.BattersFacedWarming = 5;
            return Manager(tuning).DecidePitching(state, visitor: false);
        }
        int closerId(GameState s) => s.HomeBullpen.First(w => w.Player.Role == PitcherRole.Closer).Player.Id;
        int id = closerId(Game());

        Assert.Equal(id, Decide(new ManagerTuning { CloserFromInning = 8 }, 8).ChangeToId);
        Assert.Null(Decide(new ManagerTuning(), 8).ChangeToId);
        Assert.Equal(id, Decide(new ManagerTuning(), 9).ChangeToId);
    }

    // ── Platoon emphasis ─────────────────────────────────────────────────────

    [Fact]
    public void APlatoonManagerPinchHitsForABadMatchup_AndOneWhoIgnoresPlatoonDoesNot()
    {
        // A left-handed batter against a left-hander, and an identical right-handed bat on the bench: the only difference
        // between them is the matchup.
        bool PinchHits(double weight)
        {
            var state = Game();
            state.CurrentInning = 8;
            state.IsTopOfInning = true;
            state.HomePitcher.Player.ThrowHand = ThrowHand.Left;
            var stats = new BatterStats { AtBats = 500, Hits = 150, Doubles = 28, Triples = 3, HomeRuns = 20, Walks = 55, Strikeouts = 80 };
            var batter = state.CurrentBatter;
            batter.BatHand = BatHand.Left;
            batter.Overall = stats; batter.VsLeft = stats; batter.VsRight = stats;
            var bench = state.Bench(true).Where(p => !p.IsPitcher).ToList();
            foreach (var p in bench) { p.Overall = new BatterStats { AtBats = 150, Hits = 20, Walks = 5, Strikeouts = 60 }; p.VsLeft = p.Overall; p.VsRight = p.Overall; }
            var pinch = bench.First();
            pinch.BatHand = BatHand.Right;
            pinch.Overall = stats; pinch.VsLeft = stats; pinch.VsRight = stats;
            return Manager(new ManagerTuning { PlatoonWeight = weight, PinchHitFromInning = 6, PinchHitGain = 0.01, BenchReserveScale = 0.2 })
                .MakeOffensiveDecision(state).PinchHitterPlayerId == pinch.Id;
        }

        var weights = new[] { 0.0, 1.0, 2.0, 4.0, 8.0 };
        var results = weights.Select(PinchHits).ToList();

        Assert.False(results[0], "a manager who ignores platoon has no reason to pinch hit");
        Assert.True(results[^1], "one who prizes it does");
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i] || !results[i - 1], $"weights {weights[i - 1]} -> {weights[i]}: once he pinch hits, more weight keeps him doing it");
    }

    // ── Late defensive replacements ──────────────────────────────────────────

    // A lead in the ninth, a poor glove in left field and a natural left fielder with a good one on the bench.
    private static (GameState State, Player Weak, Player Glove) ProtectingALead(int homeLead = 1)
    {
        var state = Game();
        state.CurrentInning = 9;
        state.IsTopOfInning = true;   // the home team is about to field
        state.HomeScoreByInning[0] = homeLead;
        var weak = state.LineupPlayers(false).First(p => p.Position == Position.Left).Player;
        weak.HasFieldingData = true;
        weak.Fielding[Position.Left] = new FieldingProfile(2, 0, 1);
        weak.Overall = new BatterStats { AtBats = 500, Hits = 160, HomeRuns = 25, Walks = 50, Doubles = 30 };
        var glove = state.Bench(false).First(p => !p.IsPitcher);
        glove.PrimaryPosition = Position.Left;
        glove.HasFieldingData = true;
        glove.Fielding[Position.Left] = new FieldingProfile(9, 3, 5);
        glove.Overall = new BatterStats { AtBats = 150, Hits = 38, Walks = 12, Doubles = 5 };
        return (state, weak, glove);
    }

    [Fact]
    public void AManagerWithALeadPutsInABetterGlove_AndOneWhoNeverDoesNot()
    {
        var (state, weak, glove) = ProtectingALead();

        var move = Manager(new ManagerTuning { DefensiveReplaceFromInning = 8 }).ChooseDefensiveReplacement(state, visitor: false);

        Assert.NotNull(move);
        Assert.Equal(weak.Id, move!.OutPlayerId);
        Assert.Equal(glove.Id, move.InPlayerId);
        Assert.Contains("better glove", move.Why);
        Assert.Null(Manager(new ManagerTuning()).ChooseDefensiveReplacement(state, visitor: false));
    }

    [Fact]
    public void ADefensiveReplacementNeedsALeadThatIsWorthProtectingAndAnInningThatIsLateEnough()
    {
        var (state, _, _) = ProtectingALead();
        var eager = Manager(new ManagerTuning { DefensiveReplaceFromInning = 8 });

        state.CurrentInning = 7;
        Assert.Null(eager.ChooseDefensiveReplacement(state, false));   // too early for him
        state.CurrentInning = 9;
        state.HomeScoreByInning[0] = 0;
        Assert.Null(eager.ChooseDefensiveReplacement(state, false));   // tied: nothing to protect
        state.HomeScoreByInning[0] = 9;
        Assert.Null(eager.ChooseDefensiveReplacement(state, false));   // far ahead: no need
        state.HomeScoreByInning[0] = 1;
        state.VisitorScoreByInning[0] = 3;
        Assert.Null(eager.ChooseDefensiveReplacement(state, false));   // behind: nothing to protect
    }

    [Fact]
    public void TheBatIsThePrice_SoAGoodHitterWithAnAdequateGloveStays()
    {
        var (state, weak, glove) = ProtectingALead();
        weak.Fielding[Position.Left] = new FieldingProfile(6, 2, 3);                       // an adequate glove
        glove.Fielding[Position.Left] = new FieldingProfile(7, 2, 3);                      // only a little better
        weak.Overall = new BatterStats { AtBats = 500, Hits = 190, HomeRuns = 40, Walks = 80, Doubles = 40 };   // a star bat

        // Nobody else is a bargain either, but whoever he might replace, it is not the star in left field.
        var move = Manager(new ManagerTuning { DefensiveReplaceFromInning = 8 }).ChooseDefensiveReplacement(state, false);
        Assert.True(move is null || move.OutPlayerId != weak.Id);
    }

    [Fact]
    public void AComputerSideMakesTheReplacementAsItTakesTheField_OnceAHalfInning()
    {
        int games = 0, withSubs = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)),
                new Random(seed), ManagerCatalog.Default, managerSeed: seed);
            game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), ManagerControl.Computer, ManagerControl.Computer,
                options: GameOptions.None, visitorManagerId: "tinkerer", homeManagerId: "tinkerer");
            var s = game.State!;
            for (int guard = 0; s.Status == GameStatus.InProgress && guard < 3000; guard++)
                if (game.ExecuteComputerTurn() is null) break;

            games++;
            // Every lineup still has nine players, each once, and the players taken out stay out.
            Assert.Equal(9, s.VisitorLineup.Distinct().Count());
            Assert.Equal(9, s.HomeLineup.Distinct().Count());
            Assert.DoesNotContain(s.HomeLineup, id => s.HomeBench().Contains(id));
            var subs = s.PlayByPlay.Where(e => e.Description.Contains(" replaces ")).ToList();
            if (subs.Count > 0) withSubs++;
            // At most one per team per half-inning: the manager decides as the side takes the field.
            Assert.All(subs.GroupBy(e => (e.Description[..3], e.Inning, e.IsTop)), g => Assert.Single(g));
        }

        Assert.True(withSubs > 3, $"{withSubs} of {games} games had a defensive replacement");
    }

    // ── Gus Halloran ─────────────────────────────────────────────────────────

    [Fact]
    public void TheOldSchoolManagerMakesTheBookMoveOnlyInTheNinth()
    {
        var (state, _, _) = ProtectingALead();
        var gus = new OldSchoolManager(new Random(1));

        Assert.NotNull(gus.ChooseDefensiveReplacement(state, visitor: false));
        state.CurrentInning = 8;
        Assert.Null(gus.ChooseDefensiveReplacement(state, visitor: false));
    }
}

internal static class GameStateTestExtensions
{
    /// <summary>The ids of the home players available on the bench.</summary>
    public static HashSet<int> HomeBench(this GameState s) => s.Bench(false).Select(p => p.Id).ToHashSet();
}
