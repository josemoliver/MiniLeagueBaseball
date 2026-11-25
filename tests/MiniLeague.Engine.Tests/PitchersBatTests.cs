using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;
using DoubleSwitchMove = MiniLeague.Engine.AI.ComputerManager.DoubleSwitchMove;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// The designated hitter setting, and the game without one: the pitcher in the batting order, a pinch hitter for him
/// (which forces a reliever), the double switch and pitcher bunts.
/// </summary>
public class PitchersBatTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameOptions PitchersBat => GameOptions.None with { DesignatedHitter = DesignatedHitterSetting.PitchersBat };

    private static GameService NewGame(GameOptions? options = null, string visitor = "1927-nyy.json", string home = "1975-cin.json",
        ManagerControl visitorControl = ManagerControl.Human, ManagerControl homeControl = ManagerControl.Human, int seed = 1)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed),
            ManagerCatalog.Default, managerSeed: seed);
        game.StartGame(Load(visitor), Load(home), visitorControl, homeControl, options: options ?? PitchersBat);
        return game;
    }

    private static void PlayOne(GameService game) =>
        game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision());

    // Plays until the visitors' pitcher is the man at the plate (they bat first, and he bats ninth).
    private static GameState UntilVisitorPitcherBats(GameService game)
    {
        var s = game.State!;
        for (int i = 0; i < 200 && !(s.IsTopOfInning && s.CurrentBatter.IsPitcher); i++) PlayOne(game);
        Assert.True(s.IsTopOfInning && s.CurrentBatter.IsPitcher, "the pitcher never came to bat");
        return s;
    }

    private static Player Pitcher(GameState s, bool visitor) => s.PitcherFor(visitor).Player;

    // ── The setting ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DesignatedHitterSetting.DesignatedHitter, "NL", 1969, true)]
    [InlineData(DesignatedHitterSetting.PitchersBat, "AL", 1982, false)]
    [InlineData(DesignatedHitterSetting.Historical, "NL", 1969, false)]
    [InlineData(DesignatedHitterSetting.Historical, "NL", 1980, false)]
    [InlineData(DesignatedHitterSetting.Historical, "AL", 1927, false)]
    [InlineData(DesignatedHitterSetting.Historical, "AL", 1972, false)]
    [InlineData(DesignatedHitterSetting.Historical, "AL", 1973, true)]
    [InlineData(DesignatedHitterSetting.Historical, "AL", 1982, true)]
    [InlineData(DesignatedHitterSetting.Historical, "NL", 2022, true)]
    [InlineData(DesignatedHitterSetting.Historical, "", 1975, true)]
    public void TheHomeTeamsLeagueAndYearDecideTheHistoricalRule(DesignatedHitterSetting setting, string league, int season, bool dh)
    {
        var home = new Team { League = league, Season = season };
        Assert.Equal(dh, DesignatedHitterRules.Applies(setting, home));
    }

    [Fact]
    public void TheDefaultIsTheDesignatedHitterForEveryTeam_SoNothingChangesUntilItIsChosen()
    {
        Assert.Equal(DesignatedHitterSetting.DesignatedHitter, GameOptions.None.DesignatedHitter);
        Assert.Equal(DesignatedHitterSetting.DesignatedHitter, GameOptions.Standard.DesignatedHitter);

        var s = NewGame(GameOptions.None).State!;
        Assert.True(s.DHRule);
        Assert.Contains(s.VisitorPositions.Values, p => p == Position.DH);
        Assert.DoesNotContain(s.VisitorPositions.Values, p => p == Position.P);
    }

    [Theory]
    [InlineData("1927-nyy.json", "1955-bro.json", false)]   // NL home, 1955: no designated hitter
    [InlineData("1955-bro.json", "1927-nyy.json", false)]   // AL home, 1927: none either
    [InlineData("1927-nyy.json", "1975-bos.json", true)]    // AL home, 1975: designated hitter, for both teams
    [InlineData("1975-bos.json", "1980-phi.json", false)]   // NL home, 1980: the visiting AL team bats its pitcher too
    public void HistoricalFollowsTheHomeTeam_ForBothTeams(string visitor, string home, bool dh)
    {
        var game = NewGame(GameOptions.None with { DesignatedHitter = DesignatedHitterSetting.Historical }, visitor, home);
        var s = game.State!;

        Assert.Equal(dh, s.DHRule);
        Assert.Equal(dh, s.VisitorPositions.Values.Contains(Position.DH));
        Assert.Equal(dh, s.HomePositions.Values.Contains(Position.DH));
        Assert.Equal(!dh, s.VisitorPositions.Values.Contains(Position.P));
        Assert.Equal(!dh, s.HomePositions.Values.Contains(Position.P));
    }

    // ── The batting order ────────────────────────────────────────────────────

    [Fact]
    public void WithoutADesignatedHitterTheStartingPitcherBatsNinth()
    {
        var game = NewGame();
        var s = game.State!;

        foreach (bool visitor in new[] { true, false })
        {
            var lineup = s.LineupFor(visitor);
            Assert.Equal(9, lineup.Count);
            Assert.Equal(Pitcher(s, visitor).Id, lineup[8]);
            Assert.Equal(Position.P, s.PositionsFor(visitor)[lineup[8]]);
            Assert.Equal(1, s.PositionsFor(visitor).Values.Count(p => p == Position.P));
            Assert.DoesNotContain(s.PositionsFor(visitor).Values, p => p == Position.DH);
            Assert.DoesNotContain(s.Bench(visitor), p => p.IsPitcher);
        }
    }

    [Fact]
    public void TheDesignatedHittersBatMovesToTheBench_AndThePitchersBoxScoreLineIsThere()
    {
        var team = Load("1927-nyy.json");
        var withDh = LineupRules.Default(team);
        var without = LineupRules.Default(team, designatedHitter: false, team.DefaultStarter);
        var dh = team.Roster.First(p => p.Id == withDh.Single(x => x.Position == Position.DH).PlayerId);

        Assert.DoesNotContain(without, x => x.PlayerId == dh.Id);
        Assert.Equal(withDh.Where(x => x.Position != Position.DH).Select(x => x.PlayerId), without.Take(8).Select(x => x.PlayerId));

        var s = NewGame().State!;
        Assert.Contains(s.Box.Visitor.Batters, b => b.PlayerId == Pitcher(s, true).Id && b.Pos == "P");
    }

    [Fact]
    public void TheRulesForAnEditedLineupFollowTheRuleInForce()
    {
        var team = Load("1927-nyy.json");
        var starter = team.DefaultStarter;
        var other = team.Pitchers.First(p => p.Id != starter.Id);
        var noDh = LineupRules.Default(team, false, starter);
        var withDh = LineupRules.Default(team);

        Assert.Empty(LineupRules.Validate(team, noDh, designatedHitter: false, starterId: starter.Id));
        Assert.Empty(LineupRules.Validate(team, withDh));

        // A pitcher must bat at P, the starter must be the one in the spot, and nobody else may play P.
        var wrongStarter = noDh.Select(x => x.Position == Position.P ? x with { PlayerId = other.Id } : x).ToList();
        Assert.Contains(LineupRules.Validate(team, wrongStarter, false, starter.Id), e => e.Contains("starting pitcher"));
        var hitterAtP = noDh.Select(x => x.Position == Position.Left ? x with { Position = Position.P } : x.Position == Position.P ? x with { Position = Position.Left } : x).ToList();
        Assert.NotEmpty(LineupRules.Validate(team, hitterAtP, false, starter.Id));
        Assert.NotEmpty(LineupRules.Validate(team, noDh, designatedHitter: true, starterId: null));    // a P slot with a DH
        Assert.NotEmpty(LineupRules.Validate(team, withDh, designatedHitter: false, starterId: starter.Id));   // a DH slot without
    }

    [Fact]
    public void AnEditedLineupIsFittedToTheRule_AndTheChosenStarterTakesThePitchersSpot()
    {
        var team = Load("1927-nyy.json");
        var starter = team.DefaultStarter;
        var other = team.Pitchers.First(p => p.Id != starter.Id);
        var noDh = LineupRules.Default(team, false, starter);
        var withDh = LineupRules.Default(team);

        Assert.Equal(withDh, LineupRules.Adapt(team, withDh, true, starter));            // kept
        Assert.Equal(noDh, LineupRules.Adapt(team, withDh, false, starter));             // built for the other rule: the default
        Assert.Equal(noDh, LineupRules.Adapt(team, noDh, false, starter));
        Assert.Equal(LineupRules.Default(team), LineupRules.Adapt(team, noDh, true, starter));

        var adapted = LineupRules.Adapt(team, noDh, false, other);                       // a different starter was chosen
        Assert.Equal(other.Id, adapted.Single(x => x.Position == Position.P).PlayerId);
        Assert.Equal(noDh.Select(x => x.Position), adapted.Select(x => x.Position));     // everything else as it was
    }

    [Fact]
    public void AGameCannotStartWithTheWrongLineupForTheRule()
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)));
        var team = Load("1927-nyy.json");
        var withDh = LineupRules.Default(team);

        Assert.Throws<ArgumentException>(() => game.StartGame(team, Load("1975-cin.json"), visitorLineupSlots: withDh, options: PitchersBat));
    }

    // ── Pitchers as hitters ──────────────────────────────────────────────────

    [Fact]
    public void EveryPitcherHasABattingLine_AnOrdinaryOneUnlessTheFileGivesHisOwn()
    {
        var team = Load("1927-nyy.json");
        foreach (var p in team.Pitchers)
        {
            Assert.NotNull(p.Overall);
            Assert.True(p.BuntingAbility >= 4, p.Name);
        }
        // WHY: the files carry each pitcher's real batting line now, and one reliever of few at-bats can hit .333, so judge the staff
        Assert.InRange(team.Pitchers.Sum(p => p.Overall!.Hits) / (double)team.Pitchers.Sum(p => p.Overall!.AtBats), 0.10, 0.25);

        const string json = """
        { "id": 9, "city": "T", "nickname": "N", "abbreviation": "TST", "season": 1965, "league": "NL",
          "batters": [ { "id": 1, "name": "A. Bat", "pos": "SS", "ab": 500, "h": 130 } ],
          "pitchers": [ { "id": 50, "name": "B. Arm", "era": 3.0, "ip": 200, "bats": "Left",
                          "bat": { "ab": 100, "h": 30, "d2": 5, "hr": 3, "bb": 8, "so": 20, "sh": 12 } } ] }
        """;
        var arm = TeamLoader.Parse(json).Roster.Single(p => p.Id == 50);
        Assert.Equal(100, arm.Overall!.AtBats);
        Assert.Equal(0.30, arm.Overall.BattingAverage, 2);
        Assert.Equal(BatHand.Left, arm.BatHand);
        Assert.True(arm.BuntingAbility >= 5);   // twelve sacrifice hits in 120 plate appearances: a bunter
    }

    // ── Whole games ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("analyst", "oldschool")]
    [InlineData("smallball", "tinkerer")]
    [InlineData("slugger", "analyst")]
    public void WholeGamesHoldTogether_WithThePitcherInTheOrder(string visitorId, string homeId)
    {
        int pitcherPlateAppearances = 0, pinchHitForPitcher = 0, doubleSwitches = 0, games = 0;
        for (int seed = 0; seed < 25; seed++)
        {
            var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed),
                ManagerCatalog.Default, managerSeed: seed);
            game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), ManagerControl.Computer, ManagerControl.Computer,
                options: PitchersBat with { Injuries = true }, visitorManagerId: visitorId, homeManagerId: homeId);
            var s = game.State!;
            for (int guard = 0; s.Status == GameStatus.InProgress && guard < 4000; guard++)
            {
                if (s.CurrentBatter.IsPitcher) pitcherPlateAppearances++;
                if (game.ExecuteComputerTurn() is null) break;
                AssertTheOrderIsSound(s);
            }
            games++;
            foreach (var e in s.PlayByPlay)
            {
                if (e.Description.Contains("for the pitcher")) pinchHitForPitcher++;
                if (e.Description.Contains("Double switch")) doubleSwitches++;
            }
        }

        Assert.Equal(25, games);
        Assert.True(pitcherPlateAppearances > 50, $"{pitcherPlateAppearances} pitcher plate appearances");
        // Over 25 games each of these happens, though neither is common.
        Assert.True(pinchHitForPitcher + doubleSwitches > 0, "no pinch hitter for a pitcher and no double switch in 25 games");
    }

    // The invariants after any play: nine different players, one pitcher at P, and the side in the field has its pitcher there.
    private static void AssertTheOrderIsSound(GameState s)
    {
        foreach (bool visitor in new[] { true, false })
        {
            var lineup = s.LineupFor(visitor);
            var positions = s.PositionsFor(visitor);
            Assert.Equal(9, lineup.Distinct().Count());
            Assert.Equal(1, lineup.Count(id => positions[id] == Position.P));
            Assert.Equal(lineup.Count, positions.Count);
            Assert.True(lineup.All(positions.ContainsKey));
        }
        // A side in the field has its pitcher in the order at P (a pinch hitter holds it only while that side is batting).
        bool fielding = !s.IsTopOfInning;
        var holder = s.LineupFor(fielding).First(id => s.PositionsFor(fielding)[id] == Position.P);
        if (s.Status == GameStatus.InProgress)
            Assert.Equal(s.PitcherFor(fielding).Player.Id, holder);
    }

    // ── A pinch hitter for the pitcher ───────────────────────────────────────

    [Fact]
    public void APinchHitterForThePitcherSendsHimOutOfTheGame_AndARelieverReplacesHimWhenTheSideTakesTheField()
    {
        var game = NewGame();
        var s = UntilVisitorPitcherBats(game);
        var oldPitcher = Pitcher(s, true);
        var hitter = s.Bench(true).First();
        int relievers = s.VisitorBullpen.Count;
        foreach (var w in s.VisitorBullpen) { w.IsWarming = true; w.BattersFacedWarming = 5; }

        game.PinchHit(hitter.Id);
        Assert.Equal(Position.P, s.PositionsFor(true)[hitter.Id]);                      // he holds the pitcher's place for now
        Assert.Equal(oldPitcher.Id, Pitcher(s, true).Id);                               // and the pitcher is still the one listed

        for (int i = 0; i < 100 && s.IsTopOfInning; i++) PlayOne(game);                 // the visitors finish batting

        Assert.False(s.IsTopOfInning);
        var newPitcher = Pitcher(s, true);
        Assert.NotEqual(oldPitcher.Id, newPitcher.Id);
        Assert.Equal(newPitcher.Id, s.LineupFor(true).Single(id => s.PositionsFor(true)[id] == Position.P));
        Assert.Contains(oldPitcher.Id, s.VisitorUsed);
        Assert.Contains(hitter.Id, s.VisitorUsed.Concat(s.LineupFor(true)).Where(id => id == hitter.Id).DefaultIfEmpty(-1));
        Assert.Equal(relievers - 1, s.VisitorBullpen.Count);
        Assert.Contains(s.PlayByPlay, e => e.Description.Contains("relieves"));
    }

    [Fact]
    public void YouCannotPinchHitForThePitcherWithNobodyLeftToTakeTheMound()
    {
        var game = NewGame();
        var s = UntilVisitorPitcherBats(game);
        s.VisitorBullpen.Clear();

        var ex = Assert.Throws<InvalidOperationException>(() => game.PinchHit(s.Bench(true).First().Id));
        Assert.Contains("bullpen", ex.Message);
        Assert.True(s.CurrentBatter.IsPitcher);   // nothing changed
    }

    [Fact]
    public void ThePitchingChangeMovesTheNewPitcherIntoThePitchersSpot()
    {
        var game = NewGame();
        var s = game.State!;
        for (int i = 0; i < 12; i++) PlayOne(game);
        var old = Pitcher(s, false);
        var reliever = s.HomeBullpen.First();

        game.ChangePitcher(visitor: false, reliever.Player.Id);

        Assert.Equal(reliever.Player.Id, s.LineupFor(false)[8]);
        Assert.Equal(Position.P, s.PositionsFor(false)[reliever.Player.Id]);
        Assert.DoesNotContain(old.Id, s.LineupFor(false));
        Assert.Contains(old.Id, s.HomeUsed);
    }

    [Fact]
    public void AnInjuredPitcherWhoIsBattingIsReplacedByARelieverInHisSpot_NotABenchPlayer()
    {
        var game = NewGame();
        var s = UntilVisitorPitcherBats(game);
        var hurt = s.CurrentBatter;
        s.PendingInjury = new InjuryNotice(true, hurt.Id, IsPitcher: true);

        game.ResolveInjury(s.VisitorBullpen.First().Player.Id);

        Assert.NotEqual(hurt.Id, s.CurrentBatter.Id);
        Assert.True(s.CurrentBatter.IsPitcher);
        Assert.Equal(s.CurrentBatter.Id, Pitcher(s, true).Id);
    }

    // ── The double switch ────────────────────────────────────────────────────

    [Fact]
    public void ADoubleSwitchPutsTheNewPitcherInAFielderSpotAndABenchBatInThePitchersOld()
    {
        var game = NewGame();
        var s = game.State!;
        for (int i = 0; i < 12; i++) PlayOne(game);
        var old = Pitcher(s, false);
        var reliever = s.HomeBullpen.First().Player;
        var fielder = s.LineupPlayers(false).First(x => x.Position == Position.Left).Player;
        int fielderSlot = s.LineupFor(false).IndexOf(fielder.Id);
        var bench = s.Bench(false).First();

        game.DoubleSwitch(false, reliever.Id, fielder.Id, bench.Id);

        var lineup = s.LineupFor(false);
        var positions = s.PositionsFor(false);
        Assert.Equal(reliever.Id, lineup[fielderSlot]);                  // he bats where the fielder did
        Assert.Equal(Position.P, positions[reliever.Id]);
        Assert.Equal(bench.Id, lineup[8]);                               // the bench bat has the pitcher's old spot
        Assert.Equal(Position.Left, positions[bench.Id]);                // and the fielder's position
        Assert.Equal(reliever.Id, Pitcher(s, false).Id);
        Assert.Contains(old.Id, s.HomeUsed);
        Assert.Contains(fielder.Id, s.HomeUsed);
        Assert.DoesNotContain(old.Id, lineup);
        Assert.Equal(9, lineup.Distinct().Count());
        Assert.Contains(s.PlayByPlay, e => e.Description.Contains("Double switch"));
        AssertTheOrderIsSound(s);
    }

    [Fact]
    public void ADoubleSwitchNeedsNoDesignatedHitter_AFielderToSwitch_AndNotTheBatter()
    {
        var withDh = NewGame(GameOptions.None);
        var s1 = withDh.State!;
        Assert.Throws<InvalidOperationException>(() => withDh.DoubleSwitch(false, s1.HomeBullpen.First().Player.Id,
            s1.LineupFor(false)[0], s1.Bench(false).First().Id));

        var game = NewGame();
        var s = game.State!;
        var pitcher = s.LineupFor(false)[8];
        var reliever = s.HomeBullpen.First().Player.Id;
        Assert.Throws<InvalidOperationException>(() => game.DoubleSwitch(false, reliever, pitcher, s.Bench(false).First().Id));

        // The visitors are batting: their man at the plate cannot be switched.
        var batter = s.CurrentBatter.Id;
        Assert.Throws<InvalidOperationException>(() => game.DoubleSwitch(true, s.VisitorBullpen.First().Player.Id, batter, s.Bench(true).First().Id));
    }

    [Fact]
    public void ThePitcherIsNotAFielderToReplaceOrSwap()
    {
        var game = NewGame();
        var s = game.State!;
        var pitcher = s.LineupFor(false)[8];
        var fielder = s.LineupFor(false)[0];

        Assert.Throws<InvalidOperationException>(() => game.Replace(false, pitcher, s.Bench(false).First().Id));
        Assert.Throws<InvalidOperationException>(() => game.SwapPositions(false, pitcher, fielder));
    }

    // ── What the computer managers do ────────────────────────────────────────

    private static GameState Situation()
    {
        var s = NewGame(visitorControl: ManagerControl.Computer, homeControl: ManagerControl.Computer).State!;
        s.CurrentInning = 7;
        return s;
    }

    [Fact]
    public void AManagerPinchHitsForAPitcherWhoIsBehindLate_NotWhenAheadOrWithNoRelieverToUse()
    {
        // The visitors' pitcher is up; they trail. A strong bench bat is available.
        PinchHitDecision Decide(int visitorRuns, int homeRuns, bool emptyPen)
        {
            var s = Situation();
            s.CurrentInning = 8;
            s.VisitorBatterIndex = 8;   // the ninth spot: the pitcher
            s.VisitorScoreByInning[0] = visitorRuns;
            s.HomeScoreByInning[0] = homeRuns;
            var good = new BatterStats { AtBats = 400, Hits = 130, Doubles = 25, HomeRuns = 15, Walks = 45, Strikeouts = 70 };
            foreach (var p in s.Bench(true)) { p.Overall = good; p.VsLeft = good; p.VsRight = good; }
            if (emptyPen) s.VisitorBullpen.Clear();
            return new PinchHitDecision(new ComputerManager(new Random(1)).MakeOffensiveDecision(s).PinchHitterPlayerId);
        }

        Assert.NotNull(Decide(0, 1, emptyPen: false).Id);    // behind by one in the eighth
        Assert.Null(Decide(2, 0, emptyPen: false).Id);       // ahead: he keeps his pitcher in
        Assert.Null(Decide(0, 1, emptyPen: true).Id);        // nobody to take the mound
    }

    private sealed record PinchHitDecision(int? Id);

    [Fact]
    public void TheOldSchoolManagerBuntsWithThePitcherAndPinchHitsForHimOnlyWhenBehindLate()
    {
        var s = Situation();
        s.CurrentInning = 4;   // early: the book has him bunt, and he is not pinch hit for yet
        s.VisitorBatterIndex = 8;
        s.RunnerOnFirst = new BaseRunner { Player = s.LineupPlayers(true).First().Player };
        var gus = new OldSchoolManager(new Random(1));

        var early = gus.MakeOffensiveDecision(s);
        Assert.Equal(OffensiveStrategy.SacrificeBunt, early.Strategy);
        Assert.Null(early.PinchHitterPlayerId);

        // Late and behind, the book is to hit for him; ahead, or with no reliever, he stays in.
        s.CurrentInning = 8;
        s.HomeScoreByInning[0] = 1;
        Assert.NotNull(gus.MakeOffensiveDecision(s).PinchHitterPlayerId);
        s.HomeScoreByInning[0] = 0;
        s.VisitorScoreByInning[0] = 1;
        Assert.Null(gus.MakeOffensiveDecision(s).PinchHitterPlayerId);
        s.HomeScoreByInning[0] = 3;
        s.VisitorBullpen.Clear();
        Assert.Null(gus.MakeOffensiveDecision(s).PinchHitterPlayerId);
    }

    [Fact]
    public void ThePitcherBuntsBecauseHisBatIsSoWeak()
    {
        var s = Situation();
        s.CurrentInning = 4;   // before anyone thinks of a pinch hitter
        s.VisitorBatterIndex = 8;
        s.RunnerOnFirst = new BaseRunner { Player = s.LineupPlayers(true).First().Player };
        var pitcher = s.CurrentBatter;
        var arm = s.CurrentPitcher.Player;
        double swing = ManagerDecisionModel.AtBat(s, pitcher, arm, 1.0, PitchType.Fastball, DefensiveAlignment.Standard);
        double bunt = ManagerDecisionModel.Bunt(s, pitcher, DefensiveAlignment.Standard);

        Assert.True(bunt > swing, $"bunt {bunt:0.000} vs swing {swing:0.000}");
        Assert.Equal(OffensiveStrategy.SacrificeBunt, new ComputerManager(new Random(1)).MakeOffensiveDecision(s).Strategy);
    }

    [Fact]
    public void AManagerMakesADoubleSwitchWhenThePitchersSpotIsDueSoon_AndNotWhenItIsFarOff()
    {
        // The home team is in the field; its pitcher's spot (ninth) is due soon.
        (DoubleSwitchMove? Move, GameState State) Decide(int nextBatterIndex, bool dh = false)
        {
            var s = NewGame(dh ? GameOptions.None : PitchersBat, visitorControl: ManagerControl.Computer, homeControl: ManagerControl.Computer).State!;
            s.HomeBatterIndex = nextBatterIndex;
            // A good natural left fielder on the bench, and a left fielder in the order who is a weaker hitter.
            var fielder = s.LineupPlayers(false).First(x => x.Position == Position.Left).Player;
            fielder.Overall = new BatterStats { AtBats = 400, Hits = 90, Walks = 20, Strikeouts = 80 };
            var bench = s.Bench(false).First(p => !p.IsPitcher);
            bench.PrimaryPosition = Position.Left;
            bench.Overall = new BatterStats { AtBats = 300, Hits = 100, Doubles = 20, HomeRuns = 12, Walks = 40, Strikeouts = 50 };
            var reliever = s.HomeBullpen.First().Player.Id;
            return (new ComputerManager(new Random(1)).ChooseDoubleSwitch(s, false, reliever), s);
        }

        // The ninth spot is due in one batter (batters 0..8; the next is the eighth).
        var (soon, s1) = Decide(nextBatterIndex: 7);
        Assert.NotNull(soon);
        Assert.Contains("Double switch", soon!.Why);
        Assert.DoesNotContain(soon.LineupPlayerId, new[] { s1.LineupFor(false)[8] });
        Assert.Null(Decide(nextBatterIndex: 0).Move);                 // due in eight batters: too far off
        Assert.Null(Decide(nextBatterIndex: 7, dh: true).Move);       // no double switch with a designated hitter
    }

    [Fact]
    public void ThePositionSwapsTheManagerMakesNeverMoveThePitcher()
    {
        var s = NewGame(visitorControl: ManagerControl.Computer, homeControl: ManagerControl.Computer).State!;
        var manager = new ComputerManager(new Random(1));
        var pitcherId = s.LineupFor(false)[8];

        foreach (var (a, b) in manager.BestPositionSwaps(s, false))
        {
            Assert.NotEqual(pitcherId, a);
            Assert.NotEqual(pitcherId, b);
        }
    }

    // ── Saving and the setup that picks the rule ─────────────────────────────

    [Fact]
    public void ASavedGameKeepsItsRuleAndItsOrder()
    {
        var game = NewGame(visitorControl: ManagerControl.Computer, homeControl: ManagerControl.Computer);
        var s = game.State!;
        for (int i = 0; i < 40; i++) game.ExecuteComputerTurn();

        var json = GameSnapshots.ToJson(GameSnapshots.Capture(s));
        var teams = new[] { Load("1927-nyy.json"), Load("1975-cin.json") };
        var resumed = new GameService(new AtBatResolver(new Random(2)), new ComputerManager(new Random(2)));
        resumed.Resume(GameSnapshots.FromJson(json)!, id => teams.FirstOrDefault(t => t.Id == id));
        var r = resumed.State!;

        Assert.False(r.DHRule);
        Assert.Equal(DesignatedHitterSetting.PitchersBat, r.Options.DesignatedHitter);
        Assert.Equal(s.VisitorLineup, r.VisitorLineup);
        Assert.Equal(s.HomePositions, r.HomePositions);
        AssertTheOrderIsSound(r);
        for (int i = 0; i < 20 && r.Status == GameStatus.InProgress; i++) resumed.ExecuteComputerTurn();
    }
}
