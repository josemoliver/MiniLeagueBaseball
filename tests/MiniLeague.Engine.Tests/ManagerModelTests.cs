using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// The inputs and valuation behind the computer managers: platoon matchups, steal odds, tag-ups, win-probability
/// valuation, decision explanations, seeded replays and the ratings read from the team files.
/// </summary>
public class ManagerModelTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(int seed = 1, int? managerSeed = null, string visitorId = "analyst", string homeId = "analyst")
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)),
            new Random(seed), ManagerCatalog.Default, managerSeed);
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), ManagerControl.Computer, ManagerControl.Computer,
            options: GameOptions.None, visitorManagerId: visitorId, homeManagerId: homeId);
        return game;
    }

    private static readonly BatterStats Hitter = new()
    { AtBats = 550, Hits = 160, Doubles = 30, Triples = 4, HomeRuns = 20, Walks = 60, Strikeouts = 80 };
    private static readonly PitcherStats Arm = new()
    { ERA = 3.6, InningsPitched = 220, HitsAllowed = 210, WalksAllowed = 65, Strikeouts = 140, GroundBallRate = 0.45 };

    private static OutcomeWeights Weights(BatHand bats, ThrowHand? throws) => ProbabilityTable.Build(Hitter, Arm,
        Stadium.Generic, bats, PitchType.Fastball, DefensiveAlignment.Standard, 1.0, null, throws);

    private static double Share(OutcomeWeights w, double part) =>
        part / (w.HomeRun + w.Triple + w.Double + w.Single + w.Walk + w.HitByPitch + w.Strikeout + w.GroundOut + w.FlyOut);

    // ── Platoon ──────────────────────────────────────────────────────────────

    [Fact]
    public void ABatterHitsAPitcherOfTheOtherHandBetterThanOneOfHisOwn()
    {
        var lefty = Weights(BatHand.Left, ThrowHand.Left);
        var vsRighty = Weights(BatHand.Left, ThrowHand.Right);

        Assert.True(Share(lefty, lefty.HomeRun) < Share(vsRighty, vsRighty.HomeRun));
        Assert.True(Share(lefty, lefty.Single) < Share(vsRighty, vsRighty.Single));
        Assert.True(Share(lefty, lefty.Strikeout) > Share(vsRighty, vsRighty.Strikeout));
    }

    [Fact]
    public void ALeftHandedBatterHasTheLargerSplit()
    {
        double Gap(BatHand bats, ThrowHand same, ThrowHand other)
        {
            var a = Weights(bats, other);
            var b = Weights(bats, same);
            return Share(a, a.HomeRun) / Share(b, b.HomeRun);
        }

        Assert.True(Gap(BatHand.Left, ThrowHand.Left, ThrowHand.Right) > Gap(BatHand.Right, ThrowHand.Right, ThrowHand.Left));
    }

    [Fact]
    public void ASwitchHitterAlwaysBatsFromTheFarSide_AndNoArmMeansNoPlatoon()
    {
        var switchHitter = new Player { BatHand = BatHand.Switch };
        Assert.Equal(BatHand.Right, switchHitter.BatsAgainst(ThrowHand.Left));
        Assert.Equal(BatHand.Left, switchHitter.BatsAgainst(ThrowHand.Right));

        var plain = Weights(BatHand.Left, null);
        var noArm = ProbabilityTable.Build(Hitter, Arm, Stadium.Generic, BatHand.Left, PitchType.Fastball,
            DefensiveAlignment.Standard, 1.0);
        Assert.Equal(noArm, plain);
    }

    // ── Steal odds ───────────────────────────────────────────────────────────

    [Fact]
    public void AnAverageRunnerStealsAboutAsOftenAsHisRecord_AndTheBatteryMovesIt()
    {
        var runner = new Player { SpeedRating = 3, Overall = new BatterStats { StolenBases = 30, CaughtStealing = 14 } };

        double average = BaseRunningEngine.StealSuccessChance(runner, catcherArm: 3, holdRunners: 3, pitchout: false);
        double weakBattery = BaseRunningEngine.StealSuccessChance(runner, catcherArm: 1, holdRunners: 1, pitchout: false);
        double strongBattery = BaseRunningEngine.StealSuccessChance(runner, catcherArm: 5, holdRunners: 5, pitchout: false);
        double pitchout = BaseRunningEngine.StealSuccessChance(runner, catcherArm: 3, holdRunners: 3, pitchout: true);

        Assert.InRange(average, 0.63, 0.72);           // his record is 68%, steadied toward the league's 67%
        Assert.True(weakBattery > average && average > strongBattery);
        Assert.InRange(pitchout, 0.05, 0.15);
    }

    [Fact]
    public void AShortStealRecordIsSteadiedTowardTheLeagueRate()
    {
        var oneForOne = new Player { SpeedRating = 3, Overall = new BatterStats { StolenBases = 1, CaughtStealing = 0 } };
        Assert.InRange(BaseRunningEngine.StealSuccessChance(oneForOne, 3, 3, false), 0.65, 0.75);
    }

    // ── Tag-ups ──────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyAnOutfieldFlyScoresTheRunnerFromThird()
    {
        var game = NewGame();
        var state = game.State!;
        state.Outs = 1;
        int infieldFlies = 0, outfieldFlies = 0, outfieldRuns = 0;
        for (int seed = 0; seed < 4000; seed++)
        {
            state.RunnerOnThird = new BaseRunner { Player = state.LineupPlayers(true).Skip(4).First().Player };
            var r = new AtBatResolver(new Random(seed)).Resolve(state, new OffensiveDecision(), new DefensiveDecision());
            if (r.Outcome != OutcomeType.FlyOut) continue;
            bool outfield = r.Fielder is Position.Left or Position.Center or Position.Right;
            if (outfield) { outfieldFlies++; outfieldRuns += r.RunsScored; }
            else { infieldFlies++; Assert.Equal(0, r.RunsScored); }
        }

        Assert.True(infieldFlies > 0);
        Assert.True(outfieldRuns > outfieldFlies / 3, $"{outfieldRuns} runs on {outfieldFlies} outfield flies");
    }

    // ── Win-probability valuation ────────────────────────────────────────────

    [Fact]
    public void LateAndTied_TheFirstRunIsWorthMoreThanEarly_SoGivingUpAnOutToGetItLooksBetter()
    {
        double BuntGain(int inning, bool top)
        {
            var state = NewGame().State!;
            state.CurrentInning = inning;
            state.IsTopOfInning = top;
            state.RunnerOnSecond = new BaseRunner { Player = state.LineupPlayers(top).Skip(4).First().Player };
            var batter = state.CurrentBatter;
            // WHY: his bunting rating comes from his real sacrifice hits, which differ by player; the test is about the inning.
            batter.BuntingAbility = batter.SacrificeAbility = 4;
            var pitcher = state.CurrentPitcher.Player;
            double swing = ManagerDecisionModel.AtBat(state, batter, pitcher, 1.0, PitchType.Fastball, DefensiveAlignment.Standard);
            return ManagerDecisionModel.Bunt(state, batter, DefensiveAlignment.Standard) - swing;
        }

        // Tied with a runner on second and nobody out: in the bottom of the ninth one run wins it.
        Assert.True(BuntGain(9, top: false) > BuntGain(1, top: true) + 0.05,
            $"ninth {BuntGain(9, false):0.000} vs first {BuntGain(1, true):0.000}");
    }

    [Fact]
    public void InAGameThatIsAlreadyDecided_ValuesFallBackToRunExpectancy()
    {
        var state = NewGame().State!;
        state.CurrentInning = 9;
        state.VisitorScoreByInning[0] = 14;   // up fourteen in the ninth: one more run changes nothing
        double value = ManagerDecisionModel.StateValue(state, 1, 0);
        Assert.Equal(WinProbability.ExpectedRuns(1, 0), value, 6);
    }

    // ── Explanations ─────────────────────────────────────────────────────────

    [Fact]
    public void EveryComputerCallExplainsItself_AndTheChosenCallIsTheBestItWeighed()
    {
        var game = NewGame(seed: 7, managerSeed: 7);
        var state = game.State!;
        int explained = 0;
        for (int i = 0; i < 300 && state.Status == GameStatus.InProgress; i++)
        {
            if (game.ExecuteComputerTurn() is null) break;
            var offense = game.LastOffense!;
            var defense = game.LastDefense!;
            Assert.False(string.IsNullOrWhiteSpace(offense.Why?.Summary));
            Assert.False(string.IsNullOrWhiteSpace(defense.Why?.Summary));
            Assert.Contains(offense.Why!.Considered, o => o.Call == "Swing away" && o.Gain == 0);
            if (offense.Strategy is OffensiveStrategy.SwingAgressive or OffensiveStrategy.HitAndRun or OffensiveStrategy.SacrificeBunt)
            {
                // A call is made only when it beat swinging away (personas may set a negative margin, so allow that).
                var chosen = offense.Why.Considered.Single(o => o.Call == offense.Why.Summary.Split(':')[0]);
                Assert.True(chosen.Gain > -0.05);
            }
            explained++;
        }
        Assert.True(explained > 50);
    }

    [Fact]
    public void TheOldSchoolManagerExplainsHisCallsByTheBook()
    {
        var game = NewGame(seed: 3, managerSeed: 3, visitorId: "oldschool", homeId: "oldschool");
        game.ExecuteComputerTurn();
        Assert.Matches("book|hunch|swing|run hard", game.LastOffense!.Why!.Summary);
        Assert.Contains("book", game.LastDefense!.Why!.Summary);
    }

    // ── Seeded replays ───────────────────────────────────────────────────────

    private static List<string> PlayByPlay(int managerSeed)
    {
        var game = NewGame(seed: 11, managerSeed: managerSeed, visitorId: "smallball", homeId: "tinkerer");
        var state = game.State!;
        for (int i = 0; i < 4000 && state.Status == GameStatus.InProgress; i++)
            if (game.ExecuteComputerTurn() is null) break;
        return state.PlayByPlay.Select(e => e.Description).ToList();
    }

    [Fact]
    public void ASeededComputerGameReplaysExactly()
    {
        Assert.Equal(PlayByPlay(5), PlayByPlay(5));
    }

    // ── Ratings read from the team file ──────────────────────────────────────

    [Theory]
    [InlineData(0, 600, 1)]
    [InlineData(2, 600, 2)]
    [InlineData(5, 600, 3)]
    [InlineData(9, 600, 4)]
    [InlineData(14, 600, 5)]
    [InlineData(25, 600, 6)]
    [InlineData(3, 30, 3)]      // too few plate appearances to judge
    public void SacrificeHitsSetTheBuntingRating(int sacrifices, int plateAppearances, int rating)
    {
        Assert.Equal(rating, TeamLoader.BuntRating(sacrifices, plateAppearances));
    }

    [Fact]
    public void ATeamFileWithSacrificesAndTendenciesIsRead_AndAnOlderOneStillLoads()
    {
        const string json = """
        {
          "id": 99, "city": "Test", "nickname": "Nine", "abbreviation": "TST", "season": 1965, "league": "NL",
          "tendencies": { "stealAttemptsPerGame": 0.9, "sacrificesPerGame": 0.6, "completeGameRate": 0.3, "savesPerGame": 0.2 },
          "batters": [ { "id": 1, "name": "A. Bunter", "pos": "SS", "ab": 550, "h": 140, "bb": 40, "sh": 18 },
                       { "id": 2, "name": "B. Swinger", "pos": "1B", "ab": 550, "h": 160, "bb": 60 } ],
          "pitchers": [ { "id": 50, "name": "C. Arm", "era": 3.1, "ip": 220, "holdRunners": 5 } ]
        }
        """;
        var team = TeamLoader.Parse(json);

        Assert.Equal(new TeamTendencies(0.9, 0.6, 0.3, 0.2), team.Tendencies);
        var bunter = team.Roster.Single(p => p.Id == 1);
        Assert.Equal(5, bunter.SacrificeAbility);
        Assert.Equal(5, bunter.BuntingAbility);
        Assert.Equal(3, team.Roster.Single(p => p.Id == 2).BuntingAbility);   // no "sh": the average rating
        Assert.Equal(5, team.Roster.Single(p => p.Id == 50).PitchingOverall!.HoldRunners);

        // the files in the repo carry them now (the club's real habits: 1927 Yankees, 0.99 steal attempts and 1.0 sacrifices a game)
        Assert.NotNull(Load("1927-nyy.json").Tendencies);
        Assert.InRange(Load("1927-nyy.json").Tendencies!.StealAttemptsPerGame, 0.5, 1.5);
        // and an older file without them still loads, with none
        var older = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(DataDir, "teams", "1927-nyy.json"))).RootElement.EnumerateObject()
            .Where(p => p.Name != "tendencies").ToDictionary(p => p.Name, p => p.Value));
        Assert.Null(TeamLoader.Parse(older).Tendencies);
    }

    // ── Looking a hitter ahead (bunts and intentional walks) ────────────────

    private static GameState RunnerOnFirst(int inning, BatterStats batterLine, BatterStats onDeckLine, out Player batter, out Player onDeck)
    {
        var s = NewGame().State!;
        s.CurrentInning = inning;
        s.RunnerOnFirst = new BaseRunner { Player = s.LineupPlayers(true).First().Player };
        batter = s.CurrentBatter;
        batter.Overall = batterLine; batter.VsLeft = batterLine; batter.VsRight = batterLine;
        onDeck = s.OffenseTeam.Roster.First(p => p.Id == s.VisitorLineup[(s.VisitorBatterIndex + 1) % 9]);
        onDeck.Overall = onDeckLine; onDeck.VsLeft = onDeckLine; onDeck.VsRight = onDeckLine;
        return s;
    }

    private static readonly BatterStats WeakBat = new() { AtBats = 400, Hits = 80, Walks = 20, Strikeouts = 100 };
    private static readonly BatterStats StarBat = new() { AtBats = 400, Hits = 150, HomeRuns = 35, Doubles = 30, Walks = 70, Strikeouts = 70 };

    [Fact]
    public void ABuntIsWorthMoreWhenAGoodHitterIsOnDeck()
    {
        double BuntEdge(BatterStats onDeckLine)
        {
            var s = RunnerOnFirst(3, WeakBat, onDeckLine, out var batter, out var onDeck);
            var pitcher = s.CurrentPitcher.Player;
            using var decision = ManagerDecisionModel.Decision(s);
            using (ManagerDecisionModel.Lookahead(s, onDeck, pitcher, 1.0))
                return ManagerDecisionModel.Bunt(s, batter, DefensiveAlignment.Standard)
                    - ManagerDecisionModel.AtBat(s, batter, pitcher, 1.0, PitchType.Fastball, DefensiveAlignment.Standard);
        }

        // A runner moved to second is worth more to a man who can drive him in, so the same bunt looks better in front of one.
        Assert.True(BuntEdge(StarBat) > BuntEdge(WeakBat) + 0.004, $"star {BuntEdge(StarBat):0.000} vs weak {BuntEdge(WeakBat):0.000}");
    }

    [Fact]
    public void AnIntentionalWalkIsWorthMoreInFrontOfAWeakHitterThanAStar()
    {
        double WalkValue(BatterStats onDeckLine)
        {
            var s = RunnerOnFirst(9, StarBat, onDeckLine, out _, out var onDeck);
            s.RunnerOnFirst = null;
            s.RunnerOnSecond = new BaseRunner { Player = s.LineupPlayers(true).First().Player };
            using var decision = ManagerDecisionModel.Decision(s);
            // The offense's value, so lower is better for the defense.
            return ManagerDecisionModel.WalkThenNextHitter(s, onDeck, s.CurrentPitcher.Player, 1.0);
        }

        Assert.True(WalkValue(WeakBat) < WalkValue(StarBat) - 0.05);
    }

    [Fact]
    public void LookingAheadLeavesTheGameStateAsItFoundIt()
    {
        var s = RunnerOnFirst(9, StarBat, WeakBat, out var batter, out var onDeck);
        s.RunnerOnSecond = new BaseRunner { Player = s.LineupPlayers(true).Skip(1).First().Player };
        s.Outs = 1;
        var first = s.RunnerOnFirst; var second = s.RunnerOnSecond;
        var pitcher = s.CurrentPitcher.Player;
        double before = ManagerDecisionModel.StateValue(s, 3, 1);

        ManagerDecisionModel.WalkThenNextHitter(s, onDeck, pitcher, 1.0);
        using (ManagerDecisionModel.Lookahead(s, onDeck, pitcher, 1.0))
            ManagerDecisionModel.Bunt(s, batter, DefensiveAlignment.Standard);

        Assert.Same(first, s.RunnerOnFirst);
        Assert.Same(second, s.RunnerOnSecond);
        Assert.Null(s.RunnerOnThird);
        Assert.Equal(1, s.Outs);
        Assert.Equal(before, ManagerDecisionModel.StateValue(s, 3, 1), 12);   // back to run-expectancy valuation too
    }

    // ── One decision's worth of work, done once ──────────────────────────────

    [Fact]
    public void ADecisionsCacheGivesTheSameValuesAsNoCache_AndIsGoneWhenTheDecisionEnds()
    {
        var s = RunnerOnFirst(7, StarBat, WeakBat, out var batter, out _);
        var pitcher = s.CurrentPitcher.Player;
        double Value() => ManagerDecisionModel.AtBat(s, batter, pitcher, 1.0, PitchType.Slider, DefensiveAlignment.GuardLines);

        double plain = Value();
        double cached, again;
        using (ManagerDecisionModel.Decision(s))
        {
            cached = Value();
            again = Value();   // from the cache
        }
        Assert.Equal(plain, cached, 12);
        Assert.Equal(plain, again, 12);

        // The situation changes after the decision; nothing stale may be left behind.
        s.RunnerOnSecond = new BaseRunner { Player = s.LineupPlayers(true).Skip(1).First().Player };
        double changed = Value();
        Assert.NotEqual(plain, changed);
        using (ManagerDecisionModel.Decision(s)) Assert.Equal(changed, Value(), 12);
    }

    [Fact]
    public void DecisionsNestAndShareOneScope()
    {
        var s = RunnerOnFirst(7, StarBat, WeakBat, out var batter, out _);
        var pitcher = s.CurrentPitcher.Player;
        using var outer = ManagerDecisionModel.Decision(s);
        double a = ManagerDecisionModel.AtBat(s, batter, pitcher, 1.0, PitchType.Fastball, DefensiveAlignment.Standard);
        using (ManagerDecisionModel.Decision(s))
            Assert.Equal(a, ManagerDecisionModel.AtBat(s, batter, pitcher, 1.0, PitchType.Fastball, DefensiveAlignment.Standard), 12);
        // The inner scope ending did not end the outer one: the value is still the same, from the same scope.
        Assert.Equal(a, ManagerDecisionModel.AtBat(s, batter, pitcher, 1.0, PitchType.Fastball, DefensiveAlignment.Standard), 12);
    }

    [Fact]
    public void AManagerMakesTheSameCallTwiceOnTheSameSituation()
    {
        // The cache is only a speed-up: whole seeded games must not change (see ASeededComputerGameReplaysExactly), and neither
        // may a single decision made twice on the same situation.
        for (int seed = 0; seed < 20; seed++)
        {
            var game = NewGame(seed, seed);
            var s = game.State!;
            for (int i = 0; i < 60 && s.Status == GameStatus.InProgress; i++)
            {
                var first = new ComputerManager(new Random(seed)).MakeOffensiveDecision(s);
                var second = new ComputerManager(new Random(seed)).MakeOffensiveDecision(s);
                Assert.Equal((first.Strategy, first.PinchHitterPlayerId, first.PinchRunnerPlayerId),
                    (second.Strategy, second.PinchHitterPlayerId, second.PinchRunnerPlayerId));
                if (game.ExecuteComputerTurn() is null) break;
            }
        }
    }

    // ── Shared engine rules ──────────────────────────────────────────────────

    [Fact]
    public void TheRunningRulesAreTheOnesTheEngineRolls()
    {
        // A non-aggressive runner on second scores on a single with the engine's own base chance, which is what the managers price.
        var batter = new Player { Name = "batter", SpeedRating = 3 };
        foreach (int speed in new[] { 1, 3, 5 })
        {
            var runner = new Player { Name = "runner", SpeedRating = speed };
            int scored = 0, n = 20000;
            var rng = new Random(speed);
            for (int i = 0; i < n; i++)
            {
                var (_, _, _, runs) = BaseRunningEngine.Advance(OutcomeType.Single, null, new BaseRunner { Player = runner }, null,
                    batter, OffensiveStrategy.SwingAway, rng);
                scored += runs;
            }
            double expected = RunningRules.SpeedChance(runner, RunningRules.ScoreFromSecondOnSingle);
            Assert.InRange(scored / (double)n, expected - 0.015, expected + 0.015);
        }
    }

    [Fact]
    public void TheHitAndRunAndDoublePlayRulesComeFromOnePlace()
    {
        var w = new OutcomeWeights { Strikeout = 100, Single = 100 };
        var hitAndRun = RunningRules.ApplyHitAndRun(w);
        Assert.Equal(100 * RunningRules.HitAndRunStrikeoutFactor, hitAndRun.Strikeout, 9);
        Assert.Equal(100 * RunningRules.HitAndRunSingleFactor, hitAndRun.Single, 9);

        // The speed rating moves a chance by the same number of points everywhere, and the arm takes points off.
        Assert.Equal(RunningRules.PercentPerSpeedStep / 100.0,
            RunningRules.SpeedChance(new Player { SpeedRating = 4 }, 50) - RunningRules.SpeedChance(new Player { SpeedRating = 3 }, 50), 9);
        Assert.True(RunningRules.ArmPenalty(5) > 0 && RunningRules.ArmPenalty(1) < 0 && RunningRules.ArmPenalty(3) == 0);
        Assert.True(RunningRules.DoublePlayChanceRunnerGoing < RunningRules.DoublePlayChance);
    }
}
