using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>Innings, walk-offs, extra innings, pitch counts, bullpen, who-decides, and save/resume.</summary>
public class GameFlowTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(
        ManagerControl visitor = ManagerControl.Computer, ManagerControl home = ManagerControl.Computer, int seed = 1)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), visitor, home);
        return game;
    }

    // Supplies a default decision for every human-managed side, as the UI would.
    private static void PlayNext(GameService game) => game.ExecutePlay(
        game.OffenseIsHuman ? new OffensiveDecision() : null,
        game.DefenseIsHuman ? new DefensiveDecision() : null);

    private static PlayResult Out(int outs = 1, int runs = 0) => new()
    {
        Outcome = OutcomeType.GroundOut, Description = "out", OutsRecorded = outs, RunsScored = runs, PitchesThrown = 4,
    };

    private static PlayResult Homer(int runs = 1) => new()
    {
        Outcome = OutcomeType.HomeRun, Description = "homer", RunsScored = runs, PitchesThrown = 3,
    };

    // ── Inning flow ──────────────────────────────────────────────────────────

    [Fact]
    public void HomeTeamLeadingAfterTopOfNinth_WinsWithoutBatting()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 9; s.IsTopOfInning = true; s.Outs = 2;
        s.HomeScoreByInning[0] = 3;

        game.ApplyResult(Out());

        Assert.Equal(GameStatus.Final, s.Status);
        Assert.True(s.HomeDidNotBatLastInning);
        Assert.Equal(9, s.CurrentInning);
    }

    [Fact]
    public void TiedAfterTopOfNinth_HomeTeamBats()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 9; s.IsTopOfInning = true; s.Outs = 2;

        game.ApplyResult(Out());

        Assert.Equal(GameStatus.InProgress, s.Status);
        Assert.False(s.IsTopOfInning);
    }

    [Fact]
    public void VisitorsLeadingAfterBottomOfNinth_WinAndInningStaysNine()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 9; s.IsTopOfInning = false; s.Outs = 2;
        s.VisitorScoreByInning[0] = 2;

        game.ApplyResult(Out());

        Assert.Equal(GameStatus.Final, s.Status);
        Assert.Equal(9, s.CurrentInning);
        Assert.False(s.HomeDidNotBatLastInning);
    }

    [Fact]
    public void TiedAfterBottomOfNinth_GoesToTheTenth()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 9; s.IsTopOfInning = false; s.Outs = 2;

        game.ApplyResult(Out());

        Assert.Equal(GameStatus.InProgress, s.Status);
        Assert.Equal(10, s.CurrentInning);
        Assert.True(s.IsTopOfInning);
    }

    [Fact]
    public void WalkOff_EndsTheGameImmediately()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 9; s.IsTopOfInning = false; s.Outs = 0;

        game.ApplyResult(Homer());

        Assert.Equal(GameStatus.Final, s.Status);
        Assert.Equal(1, s.HomeTotalScore);
        Assert.Equal(0, s.Outs);
    }

    [Fact]
    public void WalkOff_AlsoEndsAnExtraInningGame()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 11; s.IsTopOfInning = false;

        game.ApplyResult(Homer(2));

        Assert.Equal(GameStatus.Final, s.Status);
        Assert.Equal(2, s.HomeScoreByInning[10]);
    }

    [Fact]
    public void TakingTheLeadBeforeTheNinth_DoesNotEndTheGame()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 8; s.IsTopOfInning = false;

        game.ApplyResult(Homer());

        Assert.Equal(GameStatus.InProgress, s.Status);
    }

    [Fact]
    public void ExtraInningRuns_AreScoredInTheirOwnInning()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 11; s.IsTopOfInning = true;

        game.ApplyResult(Homer(2));

        Assert.Equal(11, s.VisitorScoreByInning.Count);
        Assert.Equal(2, s.VisitorScoreByInning[10]);
        Assert.Equal(0, s.VisitorScoreByInning[8]);
        Assert.Equal(2, s.VisitorTotalScore);
    }

    // ── Pitch counts ─────────────────────────────────────────────────────────

    [Fact]
    public void PitchCount_AddsPitchesThrown_AndStealsAreNotBattersFaced()
    {
        var game = NewGame();
        var s = game.State!;
        var pitcher = s.CurrentPitcher;

        game.ApplyResult(Out());
        Assert.Equal(4, pitcher.PitchCount);
        Assert.Equal(1, pitcher.BattersFaced);

        game.ApplyResult(new PlayResult { Outcome = OutcomeType.StolenBase, PitchesThrown = 1 });
        Assert.Equal(5, pitcher.PitchCount);
        Assert.Equal(1, pitcher.BattersFaced);
    }

    [Fact]
    public void StartersThrowARealisticNumberOfPitches()
    {
        var game = NewGame();
        var s = game.State!;
        while (s.Status == GameStatus.InProgress && s.CurrentInning <= 3)
            game.ExecuteComputerTurn();

        // About 3.5 pitches a batter, wider in a three-inning sample. Use the starter's own box-score line (he may have been relieved).
        var starter = s.Box.Home.Pitchers[0];
        Assert.True(starter.BF >= 9);
        Assert.InRange((double)starter.Pitches / starter.BF, 2.7, 4.4);
    }

    // ── Who decides ──────────────────────────────────────────────────────────

    [Fact]
    public void HumanSide_MustSupplyItsDecision()
    {
        var game = NewGame(visitor: ManagerControl.Human);  // visitors bat first

        Assert.True(game.OffenseIsHuman);
        Assert.False(game.DefenseIsHuman);
        Assert.Throws<InvalidOperationException>(() => game.ExecutePlay());
        Assert.Null(game.ExecuteComputerTurn());

        game.ExecutePlay(new OffensiveDecision());  // computer fills in the defense
    }

    [Fact]
    public void ComputerDefenseSeesTheAnnouncedPinchHitter()
    {
        var game = NewGame(visitor: ManagerControl.Human);
        var state = game.State!;
        var incoming = state.Bench(true).First();
        incoming.VsLeft = null;
        incoming.VsRight = null;
        incoming.Overall = new BatterStats { AtBats = 400, Hits = 160, HomeRuns = 80,
            Walks = 25, Strikeouts = 15 };
        var expected = new ComputerManager(new Random(1)).MakeDefensiveDecision(state, incoming);

        var play = game.ExecutePlay(new OffensiveDecision { PinchHitterPlayerId = incoming.Id });

        Assert.Equal(expected.PitchType, play.Pitch);
        Assert.Equal(incoming.Id, game.LastPrePlay!.Batter.Id);
    }

    [Fact]
    public void HumanDefense_IsAskedWhileTheComputerBats()
    {
        var game = NewGame(home: ManagerControl.Human);  // computer visitors bat first

        Assert.False(game.OffenseIsHuman);
        Assert.True(game.DefenseIsHuman);
        Assert.Throws<InvalidOperationException>(() => game.ExecutePlay());

        game.ExecutePlay(humanDefense: new DefensiveDecision { PitchType = PitchType.Curveball });
    }

    [Fact]
    public void TwoHumans_BothSupplyDecisions()
    {
        var game = NewGame(ManagerControl.Human, ManagerControl.Human);

        Assert.True(game.OffenseIsHuman && game.DefenseIsHuman);
        Assert.Throws<InvalidOperationException>(() => game.ExecutePlay(new OffensiveDecision()));
        game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision());
    }

    [Fact]
    public void ChosenAlignment_IsRecordedForTheFieldingSide()
    {
        var game = NewGame();
        game.ExecuteAtBat(new OffensiveDecision(),
            new DefensiveDecision { Alignment = DefensiveAlignment.InfieldIn });

        Assert.Equal(DefensiveAlignment.InfieldIn, game.State!.HomeAlignment);
    }

    // ── Bullpen ──────────────────────────────────────────────────────────────

    [Fact]
    public void StartGame_BuildsABullpenOfRestingRelievers_AndMarksStartersUsed()
    {
        var s = NewGame().State!;

        Assert.Equal(3, s.VisitorBullpen.Count);  // closer + two relievers
        Assert.All(s.VisitorBullpen, w => Assert.False(w.IsWarming));
        Assert.All(s.VisitorBullpen, w => Assert.Equal("Resting", w.Status));
        Assert.Contains(s.VisitorPitcher.Player.Id, s.VisitorUsed);
    }

    [Fact]
    public void WarmingRelievers_GetReadyAsTheirTeamsPitcherFacesBatters()
    {
        var game = NewGame();
        var s = game.State!;
        var home = s.HomeBullpen[0];
        var visitor = s.VisitorBullpen[0];
        game.StartWarming(false, home.Player.Id);
        Assert.Equal("Warming (0/4)", home.Status);

        for (int i = 0; i < 4; i++) game.ApplyResult(Out(0));  // top half: the home pitcher faces batters

        Assert.Equal(4, home.BattersFacedWarming);
        Assert.Equal("Ready", home.Status);
        Assert.Equal(0, visitor.BattersFacedWarming);          // never told to warm up
    }

    [Fact]
    public void ChangePitcher_SwapsInTheRelieverAndRetiresTheOldPitcher()
    {
        var game = NewGame();
        var s = game.State!;
        var starter = s.HomePitcher.Player;
        var reliever = s.HomeBullpen[0];
        game.StartWarming(false, reliever.Player.Id);
        for (int i = 0; i < 4; i++) game.ApplyResult(Out(0));

        game.ChangePitcher(false, reliever.Player.Id);

        Assert.Equal(reliever.Player.Id, s.HomePitcher.Player.Id);
        Assert.Equal(1.0, s.HomePitcher.EntryModifier);       // warm: no penalty
        Assert.Equal(0, s.HomePitcher.PitchCount);
        Assert.DoesNotContain(s.HomeBullpen, w => w.Player.Id == reliever.Player.Id);
        Assert.Contains(starter.Id, s.HomeUsed);
        Assert.Contains("relieves", s.PlayByPlay[0].Description);
        Assert.Throws<InvalidOperationException>(() => game.ChangePitcher(false, starter.Id));  // cannot return
    }

    [Fact]
    public void AColdReliever_PitchesBelowParForHisFirstBatters()
    {
        var game = NewGame();
        var s = game.State!;
        game.ChangePitcher(false, s.HomeBullpen[0].Player.Id);

        Assert.True(s.HomePitcher.EntryModifier < 1.0);
        Assert.True(FatigueModel.GetModifier(s.HomePitcher) < 0.8);

        s.HomePitcher.BattersFaced = FatigueModel.SettleInBatters;
        Assert.Equal(1.0, FatigueModel.GetModifier(s.HomePitcher));  // settled in
    }

    [Fact]
    public void DefensiveDecision_CanCallForAReliever()
    {
        var game = NewGame();
        var s = game.State!;
        var reliever = s.HomeBullpen[0].Player;

        game.ExecuteAtBat(new OffensiveDecision(), new DefensiveDecision { RelieverPlayerId = reliever.Id });

        Assert.Equal(reliever.Id, s.HomePitcher.Player.Id);
    }

    // ── Computer bullpen management ──────────────────────────────────────────

    [Fact]
    public void Computer_StartsWarmingOnceTheStarterIsPastHisFirstThreshold()
    {
        var game = NewGame();
        var s = game.State!;
        s.HomePitcher.BattersFaced = 20;
        // past the first threshold, not yet tiring (a starter's first threshold is his own stamina, from his real starts)
        s.HomePitcher.PitchCount = (int)FatigueModel.Stamina(s.HomePitcher.Player.PitchingOverall) + 5;

        var move = new ComputerManager(new Random(1)).DecidePitching(s, visitor: false);

        Assert.NotNull(move.StartWarmingId);
        Assert.Null(move.ChangeToId);
    }

    [Fact]
    public void Computer_PullsATiringStarterForAReadyReliever()
    {
        var game = NewGame();
        var s = game.State!;
        s.HomePitcher.BattersFaced = 28;
        s.HomePitcher.PitchCount = 200;
        var ready = s.HomeBullpen.First(w => w.Player.Role != PitcherRole.Closer);
        ready.IsWarming = true; ready.BattersFacedWarming = 5;

        var move = new ComputerManager(new Random(1)).DecidePitching(s, visitor: false);

        Assert.Equal(ready.Player.Id, move.ChangeToId);
    }

    [Fact]
    public void Computer_BringsInAWarmCloserForASaveInTheNinth()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 9;
        s.HomeScoreByInning[0] = 2;  // home leads by two
        var closer = s.HomeBullpen.First(w => w.Player.Role == PitcherRole.Closer);
        closer.IsWarming = true; closer.BattersFacedWarming = 6;

        var move = new ComputerManager(new Random(1)).DecidePitching(s, visitor: false);

        Assert.Equal(closer.Player.Id, move.ChangeToId);
    }

    [Fact]
    public void Computer_DoesNotUseTheCloserOutsideASaveSpot()
    {
        var game = NewGame();
        var s = game.State!;
        s.HomePitcher.BattersFaced = 30;
        s.HomePitcher.PitchCount = 200;
        foreach (var w in s.HomeBullpen) { w.IsWarming = true; w.BattersFacedWarming = 6; }

        var move = new ComputerManager(new Random(1)).DecidePitching(s, visitor: false);

        Assert.NotNull(move.ChangeToId);
        Assert.NotEqual(PitcherRole.Closer, s.HomeBullpen.First(w => w.Player.Id == move.ChangeToId).Player.Role);
    }

    // ── Whole games ──────────────────────────────────────────────────────────

    private static readonly string[] AllTeamFiles =
        Directory.Exists(Path.Combine(DataDir, "teams"))
            ? Directory.GetFiles(Path.Combine(DataDir, "teams"), "*.json").Select(f => Path.GetFileName(f)!).ToArray()
            : [];

    [Fact]
    public void ManyComputerGames_AlwaysEndLegallyWithRealisticBullpenUse()
    {
        int games = 0, extraInnings = 0, pitchingChanges = 0, walkOffs = 0;

        for (int seed = 0; seed < 120; seed++)
        {
            var rng = new Random(seed);
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng));
            var visitor = Load(AllTeamFiles[seed % AllTeamFiles.Length]);
            var home = Load(AllTeamFiles[(seed * 7 + 3) % AllTeamFiles.Length]);
            game.StartGame(visitor, home, ManagerControl.Computer, ManagerControl.Computer);
            var s = game.State!;

            for (int plays = 0; s.Status == GameStatus.InProgress; plays++)
            {
                Assert.True(plays < 4000, $"seed {seed}: game never ended");
                game.ExecuteComputerTurn();
            }

            games++;
            Assert.NotEqual(s.VisitorTotalScore, s.HomeTotalScore);
            Assert.True(s.CurrentInning >= 9);
            if (s.CurrentInning > 9) extraInnings++;
            Assert.Equal(s.VisitorTotalScore, Enumerable.Range(1, s.CurrentInning).Sum(i => s.RunsInInning(true, i)));

            // Home leads on a walk-off or when it did not need the bottom of the 9th; visitors win only after a full inning.
            if (s.VisitorTotalScore > s.HomeTotalScore) Assert.False(s.IsTopOfInning);
            if (!s.IsTopOfInning && s.HomeTotalScore > s.VisitorTotalScore) walkOffs++;

            pitchingChanges += s.VisitorUsed.Count(id => s.VisitorTeam.Roster.First(p => p.Id == id).IsPitcher) - 1
                             + s.HomeUsed.Count(id => s.HomeTeam.Roster.First(p => p.Id == id).IsPitcher) - 1;

            // Nobody the computer used can still be in a bullpen.
            Assert.DoesNotContain(s.HomeBullpen, w => s.HomeUsed.Contains(w.Player.Id));
            Assert.DoesNotContain(s.VisitorBullpen, w => s.VisitorUsed.Contains(w.Player.Id));
        }

        Assert.Equal(120, games);
        Assert.True(extraInnings > 0, "expected at least one extra-inning game in 120");
        Assert.True(walkOffs > 0, "expected at least one walk-off in 120");
        // With fatigue and hooks working, relievers appear regularly but starters still go deep
        // (the ace starts every game, so this is a floor, not a real-world average).
        // NOTE: lowered from 0.25 when aggressive sends could be thrown out and the infield in began holding the
        // runner on third: fewer runs score, so fewer starters are shelled and pulled.
        // NOTE: lowered from 0.2 when pitch counts came down to about 3.5 a batter and starters were given stamina from their
        // real starts: the aces of 1927 to 1982 finished far more often than a modern starter.
        Assert.InRange(pitchingChanges / (games * 2.0), 0.15, 3.0);
    }

    // ── Save and resume ──────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_RoundTripsThroughJsonAndTheGameCanContinue()
    {
        var rng = new Random(5);
        var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng));
        var visitor = Load("1927-nyy.json");
        var home = Load("1975-cin.json");
        game.StartGame(visitor, home, ManagerControl.Human, ManagerControl.Computer);
        var s = game.State!;

        // Play into the middle of a game with a substitution, a warming reliever and a runner on.
        while (s.CurrentInning < 4) PlayNext(game);
        game.PinchHit(s.Bench(true).First().Id);
        game.StartWarming(true, s.VisitorBullpen[0].Player.Id);
        s.RunnerOnSecond = new BaseRunner { Player = s.LineupPlayers(true).Skip(2).First().Player, BattingSlot = 2 };

        string json = GameSnapshots.ToJson(GameSnapshots.Capture(s));
        var snapshot = GameSnapshots.FromJson(json)!;
        var resumed = new GameService(new AtBatResolver(new Random(9)), new ComputerManager(new Random(9)));
        resumed.Resume(snapshot, id => new[] { visitor, home }.FirstOrDefault(t => t.Id == id));
        var r = resumed.State!;

        Assert.Equal(s.VisitorScoreByInning, r.VisitorScoreByInning);
        Assert.Equal(s.HomeScoreByInning, r.HomeScoreByInning);
        Assert.Equal((s.CurrentInning, s.IsTopOfInning, s.Outs), (r.CurrentInning, r.IsTopOfInning, r.Outs));
        Assert.Equal(s.VisitorLineup, r.VisitorLineup);
        Assert.Equal(s.VisitorPositions, r.VisitorPositions);
        Assert.Equal(s.VisitorUsed.Order(), r.VisitorUsed.Order());
        Assert.Equal(s.RunnerOnSecond!.Player.Id, r.RunnerOnSecond!.Player.Id);
        Assert.Equal(s.HomePitcher.PitchCount, r.HomePitcher.PitchCount);
        Assert.Equal(s.HomePitcher.Player.Id, r.HomePitcher.Player.Id);
        Assert.Equal(s.VisitorBullpen.Select(w => (w.Player.Id, w.IsWarming)), r.VisitorBullpen.Select(w => (w.Player.Id, w.IsWarming)));
        Assert.Equal(s.PlayByPlay.Select(e => e.Description), r.PlayByPlay.Select(e => e.Description));
        Assert.Equal(snapshot.Description, GameSnapshots.Capture(r).Description);

        for (int plays = 0; r.Status == GameStatus.InProgress; plays++)
        {
            Assert.True(plays < 4000);
            PlayNext(resumed);
        }
        Assert.Equal(GameStatus.Final, r.Status);
    }

    [Fact]
    public void Snapshot_TrimsLongPlayLogs()
    {
        var s = NewGame().State!;
        for (int i = 0; i < 500; i++)
            s.PlayByPlay.Add(new PlayEvent { Description = "x" });

        Assert.Equal(GameSnapshots.MaxLoggedPlays, GameSnapshots.Capture(s).PlayByPlay.Count);
    }

    [Fact]
    public void Resume_RejectsSavesThatNoLongerMatchTheRosters()
    {
        var game = NewGame();
        var visitor = game.State!.VisitorTeam;
        var home = game.State.HomeTeam;
        var snapshot = GameSnapshots.Capture(game.State);

        Assert.Throws<InvalidOperationException>(() =>
            new GameService(new AtBatResolver(), new ComputerManager()).Resume(snapshot, _ => null));

        visitor.Roster.RemoveAll(p => p.Id == snapshot.VisitorLineup[0]);
        Assert.Throws<InvalidOperationException>(() =>
            new GameService(new AtBatResolver(), new ComputerManager())
                .Resume(snapshot, id => id == visitor.Id ? visitor : home));
    }

    [Fact]
    public void FromJson_ReturnsNullForGarbageOrOtherVersions()
    {
        Assert.Null(GameSnapshots.FromJson("not json"));
        Assert.Null(GameSnapshots.FromJson("{ \"version\": 99 }"));
    }
}
