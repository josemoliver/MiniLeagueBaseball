using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>Fielding (range, errors, arm), ballparks, and the data files behind them.</summary>
public class FieldingAndParkTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static Player Fielder(int range = 5, int errors = 2, int arm = 3, Position pos = Position.Short) => new()
    {
        Id = 1, Name = "F", HasFieldingData = true,
        Fielding = new() { [pos] = new FieldingProfile(range, errors, arm) },
    };

    // ── Player fielding data ─────────────────────────────────────────────────

    [Fact]
    public void FieldingAt_NoData_IsAverageEverywhere()
    {
        var p = new Player { ArmStrength = 5 };

        Assert.Equal(FieldingProfile.Average, p.FieldingAt(Position.Short));
        Assert.True(p.IsNaturalAt(Position.C));
        Assert.Equal(5, p.ArmAt(Position.C));   // falls back to the legacy arm rating
    }

    [Fact]
    public void FieldingAt_PositionsHePlays_UseHisRatings_OthersAreOutOfPosition()
    {
        var p = Fielder(range: 8, errors: 3, arm: 4, pos: Position.Short);

        Assert.Equal(new FieldingProfile(8, 3, 4), p.FieldingAt(Position.Short));
        Assert.False(p.IsNaturalAt(Position.Second));
        Assert.Equal(FieldingProfile.OutOfPosition(Position.Second), p.FieldingAt(Position.Second));
        Assert.True(p.FieldingAt(Position.Second).Value < p.FieldingAt(Position.Short).Value);
        Assert.Equal(FieldingProfile.Average, p.FieldingAt(Position.DH));   // a DH does not field
    }

    [Fact]
    public void ABatWithAnEmptyFieldingMap_IsPoorAnywhereInTheField()
    {
        var bat = new Player { HasFieldingData = true };   // generated data: he never played the field

        Assert.False(bat.IsNaturalAt(Position.Third));
        Assert.True(bat.FieldingAt(Position.Third).ErrorRating <= 1);
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    [Fact]
    public void ErrorChance_AnAverageDefenseErrsAtTheEraRate()
    {
        var rng = new Random(11);
        var avg = Fielder();
        double era = 1.6;   // errors per team game
        int errors = 0, trials = 200_000;

        for (int i = 0; i < trials; i++)
        {
            var outcome = rng.NextDouble() < 0.6 ? OutcomeType.GroundOut : OutcomeType.FlyOut;
            var pos = DefenseModel.PickFielder(outcome, rng);
            var who = Fielder(pos: pos);   // average ratings at whatever position gets the ball
            if (rng.NextDouble() < DefenseModel.ErrorChance(who, pos, era)) errors++;
        }

        double expected = era / DefenseModel.BattedBallChances(era, 4.0);
        Assert.InRange(errors / (double)trials, expected * 0.85, expected * 1.15);
    }

    [Fact]
    public void ErrorChance_BetterGlovesErrLess_AndInfieldersErrMoreThanOutfielders()
    {
        double era = 1.0;
        var gold = Fielder(errors: 3, pos: Position.Short);
        var poor = Fielder(errors: 0, pos: Position.Short);
        Assert.True(DefenseModel.ErrorChance(gold, Position.Short, era) * 3
                    < DefenseModel.ErrorChance(poor, Position.Short, era));

        var ss = Fielder(pos: Position.Short);
        var cf = Fielder(pos: Position.Center);
        Assert.True(DefenseModel.ErrorChance(ss, Position.Short, era)
                    > DefenseModel.ErrorChance(cf, Position.Center, era));
    }

    [Fact]
    public void ErrorChance_APlayerOutOfPositionErrsMore()
    {
        var natural = Fielder(pos: Position.Third);
        var misplaced = Fielder(pos: Position.First);   // a first baseman at third

        Assert.True(DefenseModel.ErrorChance(misplaced, Position.Third, 1.0)
                    > DefenseModel.ErrorChance(natural, Position.Third, 1.0) * 1.4);
    }

    [Fact]
    public void ErrorsAreMadeInRealGames_CloseToTheEraRate()
    {
        double errors = 0, teamGames = 0, expected = 0;
        for (int seed = 0; seed < 60; seed++)
        {
            var rng = new Random(seed);
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng), rng);
            var visitor = Load("1927-nyy.json");
            var home = Load("1982-mil.json");
            game.StartGame(visitor, home, ManagerControl.Computer, ManagerControl.Computer);
            var s = game.State!;
            while (s.Status == GameStatus.InProgress) game.ExecuteComputerTurn();

            errors += s.Box.Visitor.Errors + s.Box.Home.Errors;
            teamGames += 2;
            expected += visitor.EraErrorsPerGame + home.EraErrorsPerGame;
        }

        Assert.InRange(errors, expected * 0.6, expected * 1.5);   // wide: errors depend on who is in the field
        Assert.True(errors > 0);
    }

    [Fact]
    public void OnError_EveryoneMovesUpOneBase_AndTheRunnerFromThirdScores()
    {
        var a = new BaseRunner { Player = new Player { Id = 1 } };
        var b = new BaseRunner { Player = new Player { Id = 2 } };
        var c = new BaseRunner { Player = new Player { Id = 3 } };
        var batter = new Player { Id = 9 };

        var (first, second, third, runs) = BaseRunningEngine.OnError(a, b, c, batter);

        Assert.Equal(9, first!.Player.Id);
        Assert.Equal(1, second!.Player.Id);
        Assert.Equal(2, third!.Player.Id);
        Assert.Equal(1, runs);
    }

    // ── Base running: arms, double plays, and conservation of runners ─────────

    [Fact]
    public void AStrongOutfieldArm_HoldsRunnersMoreOften()
    {
        int Scored(int armPenalty)
        {
            var rng = new Random(5);
            int scored = 0;
            for (int i = 0; i < 6000; i++)
            {
                var runner = new BaseRunner { Player = new Player { SpeedRating = 3 } };
                var (_, _, _, runs) = BaseRunningEngine.Advance(OutcomeType.Single, null, runner, null,
                    new Player(), OffensiveStrategy.SwingAgressive, rng, armPenalty);
                scored += runs;
            }
            return scored;
        }

        Assert.True(Scored(armPenalty: 12) < Scored(armPenalty: 0) - 300);
        Assert.True(Scored(armPenalty: -12) > Scored(armPenalty: 0) + 300);
    }

    [Fact]
    public void GroundOut_DoublePlay_RecordsTwoOutsAndAdvancesTheOthers()
    {
        var first = new BaseRunner { Player = new Player { Id = 1 } };
        var second = new BaseRunner { Player = new Player { Id = 2 } };
        var rng = new Random(2);
        (BaseRunner?, BaseRunner?, BaseRunner?, int, int)? dp = null;
        for (int i = 0; i < 200 && dp is null; i++)
        {
            var r = BaseRunningEngine.GroundOut(first, second, null, DefensiveAlignment.Standard, 0, rng);
            if (r.outs == 2) dp = (r.first, r.second, r.third, r.runs, r.outs);
        }

        Assert.NotNull(dp);
        Assert.Null(dp!.Value.Item1);                       // batter out
        Assert.Null(dp.Value.Item2);                        // runner from first out
        Assert.Equal(2, dp.Value.Item3!.Player.Id);         // runner from second moved to third
    }

    [Fact]
    public void NoRunScoresWhenTheThirdOutEndsTheInning()
    {
        var third = new BaseRunner { Player = new Player { Id = 3 } };

        var plain = BaseRunningEngine.GroundOut(null, null, third, DefensiveAlignment.Standard, 2, new Random(1));
        Assert.Equal(0, plain.runs);

        var withOneOut = BaseRunningEngine.GroundOut(null, null, third, DefensiveAlignment.Standard, 1, new Random(1));
        Assert.Equal(1, withOneOut.runs);
    }

    [Fact]
    public void EveryPlay_AccountsForEveryRunner_NobodyAppearsTwiceOrVanishes()
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var rng = new Random(seed);
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng), rng);
            game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer);
            var s = game.State!;

            while (s.Status == GameStatus.InProgress)
            {
                int before = new[] { s.RunnerOnFirst, s.RunnerOnSecond, s.RunnerOnThird }.Count(r => r is not null);
                int outsBefore = s.Outs;
                var result = game.ExecuteComputerTurn()!;
                bool plateAppearance = result.Outcome is not (OutcomeType.StolenBase or OutcomeType.CaughtStealing);
                var after = new[] { result.NewFirst, result.NewSecond, result.NewThird }.Where(r => r is not null).ToList();

                Assert.Equal(after.Count, after.Select(r => r!.Player.Id).Distinct().Count());
                if (outsBefore + result.OutsRecorded >= 3) continue;   // inning over: leftover runners are irrelevant
                Assert.True(before + (plateAppearance ? 1 : 0) == after.Count + result.RunsScored + result.OutsRecorded,
                    $"{result.Outcome}: {before} on + batter != {after.Count} on + {result.RunsScored} runs + {result.OutsRecorded} outs");
            }
        }
    }

    // ── Park and fielders in the probability table ───────────────────────────

    private static OutcomeWeights Weights(Stadium stadium, BatHand hand = BatHand.Left, DefenseRatings? defense = null)
    {
        var batter = new BatterStats { AtBats = 500, Hits = 150, HomeRuns = 25, Doubles = 30, Triples = 5, Walks = 50, Strikeouts = 90 };
        var pitcher = new PitcherStats { ERA = 4.0, InningsPitched = 200, HitsAllowed = 190, WalksAllowed = 60, Strikeouts = 150 };
        return ProbabilityTable.Build(batter, pitcher, stadium, hand, PitchType.Fastball,
            DefensiveAlignment.Standard, 1.0, defense);
    }

    [Fact]
    public void ShortFenceParks_BoostHomeRuns_ForTheBatterWhoPullsToThatField()
    {
        var porch = new Stadium { HomeRunFactorLeft = 1.25, HomeRunFactorRight = 0.85 };

        double lefty = Weights(porch, BatHand.Left).HomeRun / Weights(Stadium.Generic, BatHand.Left).HomeRun;
        double righty = Weights(porch, BatHand.Right).HomeRun / Weights(Stadium.Generic, BatHand.Right).HomeRun;

        Assert.Equal(1.25, lefty, 2);
        Assert.Equal(0.85, righty, 2);
    }

    [Fact]
    public void HittersParks_RaiseHits_PitchersParks_LowerThem_AndTurfSpeedsUpSingles()
    {
        var neutral = Weights(Stadium.Generic);
        var hitters = Weights(new Stadium { RunsFactor = 1.1 });
        var pitchers = Weights(new Stadium { RunsFactor = 0.9 });
        var turf = Weights(new Stadium { SinglesFactor = 1.04, DoublesTriplesFactor = 1.1 });

        Assert.True(hitters.Single > neutral.Single && hitters.Double > neutral.Double);
        Assert.True(pitchers.Single < neutral.Single);
        Assert.True(turf.Single > neutral.Single && turf.Triple > neutral.Triple);
        Assert.Equal(neutral.Strikeout, hitters.Strikeout, 6);   // only hits are affected
    }

    [Fact]
    public void BetterRange_TurnsHitsIntoOuts()
    {
        var good = Weights(Stadium.Generic, defense: new DefenseRatings(8, 8, 3));
        var poor = Weights(Stadium.Generic, defense: new DefenseRatings(2, 2, 3));
        var avg = Weights(Stadium.Generic);

        Assert.True(good.Single < avg.Single && avg.Single < poor.Single);
        Assert.True(good.Double < poor.Double);
        Assert.Equal(avg.HomeRun, good.HomeRun, 9);   // nobody catches a homer
    }

    [Fact]
    public void Ratings_ComeFromTheFieldersActuallyPlayingEachPosition()
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)));
        game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer);
        var s = game.State!;

        var ratings = DefenseModel.RatingsFor(s);   // the home team (Boston) is in the field in the top half

        Assert.InRange(ratings.InfieldRange, 1, 9);
        Assert.InRange(ratings.OutfieldArm, 1, 5);
        Assert.NotEqual(DefenseRatings.Average, ratings);
    }

    // ── Data files ───────────────────────────────────────────────────────────

    [Fact]
    public void TeamFiles_CarryFieldingBallparkAndEraData()
    {
        var team = Load("1975-bos.json");

        Assert.Equal("fenway-1975", team.StadiumId);
        Assert.InRange(team.EraErrorsPerGame, 0.4, 2.5);
        var evans = team.Roster.First(p => p.Name == "D. Evans");
        Assert.True(evans.HasFieldingData);
        Assert.True(evans.Fielding.ContainsKey(Position.Right));
        Assert.Equal(5, evans.FieldingAt(Position.Right).Arm);   // the famous arm
    }

    [Fact]
    public void ShippedStadiums_ParseAndEveryTeamHasAHomePark()
    {
        var stadiums = StadiumLoader.ParseAll(File.ReadAllText(Path.Combine(DataDir, "stadiums.json")));
        var ids = stadiums.Select(x => x.Id).ToHashSet();
        Assert.Equal(stadiums.Count, ids.Count);

        foreach (var s in stadiums)
        {
            Assert.InRange(s.LeftField, 250, 400);
            Assert.InRange(s.Center, 380, 500);
            Assert.InRange(s.RunsFactor, 0.85, 1.15);
            Assert.InRange(s.HomeRunFactorLeft, 0.8, 1.25);
            Assert.InRange(s.HomeRunFactorRight, 0.8, 1.25);
            Assert.InRange(s.DoublesTriplesFactor, 0.85, 1.3);
        }

        var index = TeamLoader.ParseIndex(File.ReadAllText(Path.Combine(DataDir, "teams.json")));
        foreach (var path in index)
        {
            var team = TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, path)));
            Assert.Contains(team.StadiumId, ids);
        }
    }

    [Fact]
    public void FenwayHelpsLeftHandedHomerAndYankeeStadiumInTheTwentiesHelpsBoth()
    {
        var stadiums = StadiumLoader.ParseAll(File.ReadAllText(Path.Combine(DataDir, "stadiums.json")));
        var fenway = stadiums.First(x => x.Id == "fenway-1975");
        var yankee = stadiums.First(x => x.Id == "yankee-1927");

        Assert.Equal(37, fenway.LeftWallHeight);
        Assert.True(fenway.HomeRunFactorLeft > fenway.HomeRunFactorRight);   // short right-field line
        Assert.True(yankee.HomeRunFactorLeft > 1.0 && yankee.HomeRunFactorRight > 1.0);
        Assert.True(yankee.DoublesTriplesFactor > 1.2);                       // cavernous gaps
    }

    [Fact]
    public void StadiumLoader_ReadsAWallColour_AndRefusesOneThatIsNotAColour()
    {
        var painted = Assert.Single(StadiumLoader.ParseAll("""{ "stadiums": [ { "id": "g", "wallColor": "#2a6e47" } ] }"""));
        var plain = Assert.Single(StadiumLoader.ParseAll("""{ "stadiums": [ { "id": "p" } ] }"""));

        Assert.Equal("#2a6e47", painted.WallColor);
        Assert.Null(plain.WallColor);   // most parks: the animated field keeps its own wall
        foreach (var bad in new[] { "green", "#2a6e4", "2a6e47", "#zzzzzz" })
        {
            var ex = Assert.Throws<System.Text.Json.JsonException>(() =>
                StadiumLoader.ParseAll($$"""{ "stadiums": [ { "id": "bad-park", "wallColor": "{{bad}}" } ] }"""));
            Assert.Contains("bad-park", ex.Message);   // it says which park
        }
    }

    // A wall colour is only shipped where a source supports it (see ATTRIBUTION.md): these two, and no others.
    [Fact]
    public void OnlyTheParksWithASourceForTheirWallHaveAWallColour()
    {
        var stadiums = StadiumLoader.ParseAll(File.ReadAllText(Path.Combine(DataDir, "stadiums.json")));

        var painted = stadiums.Where(s => s.WallColor is not null).Select(s => s.Id).Order().ToArray();

        Assert.Equal(["fenway-1975", "shea-1969"], painted);
    }

    [Fact]
    public void StadiumLoader_ReadsAllFieldsAndDefaultsTheRest()
    {
        var parsed = StadiumLoader.ParseAll("""
            { "stadiums": [ { "id": "x", "name": "X Park", "city": "Xville", "left": 300, "center": 420,
                              "hrLeft": 1.1, "turf": true, "runs": 1.05 } ] }
            """);

        var x = Assert.Single(parsed);
        Assert.Equal(("x", "X Park", 300, 420), (x.Id, x.Name, x.LeftField, x.Center));
        Assert.Equal(330, x.RightField);   // default
        Assert.Equal(1.1, x.HomeRunFactorLeft);
        Assert.True(x.IsTurf);
        Assert.Equal(1.05, x.RunsFactor);
        Assert.Throws<System.Text.Json.JsonException>(() => StadiumLoader.ParseAll("""{ "stadiums": [ {} ] }"""));
    }

    [Fact]
    public void TeamLoader_RejectsMalformedFieldingLines()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => TeamLoader.Parse("""
            { "id": 1, "batters": [ { "id": 1, "name": "B", "pos": "SS", "field": { "SS": [5, 2] } } ], "pitchers": [] }
            """));
    }
}
