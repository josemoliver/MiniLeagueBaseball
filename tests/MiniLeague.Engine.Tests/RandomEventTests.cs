using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>Injuries, manager ejections and rain delays.</summary>
public class RandomEventTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    /// <summary>Returns the scripted values in turn, so an event can be forced to happen (or not) and to pick a chosen target.</summary>
    private sealed class ScriptedRandom(params double[] values) : Random
    {
        private int _next;
        public override double NextDouble() => values[_next++ % values.Length];
        public override int Next(int minValue, int maxValue) => minValue;
    }

    private static GameService NewGame(GameOptions options, Random events,
        ManagerControl visitor = ManagerControl.Human, ManagerControl home = ManagerControl.Computer)
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)), events);
        game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), visitor, home, options: options);
        return game;
    }

    private static PlayResult Out() => new() { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 };

    // ── Switches ─────────────────────────────────────────────────────────────

    [Fact]
    public void WithEventsOff_NothingHappensEvenIfTheDiceAreRiggedToAlwaysTrigger()
    {
        var game = NewGame(GameOptions.None, new ScriptedRandom(0.0), ManagerControl.Computer);
        var s = game.State!;

        for (int i = 0; s.Status == GameStatus.InProgress && i < 3000; i++) game.ExecuteComputerTurn();

        Assert.Null(s.PendingInjury);
        Assert.Equal(0, s.RainDelays);
        Assert.False(s.VisitorManagerEjected || s.HomeManagerEjected || s.EndedByRain);
        Assert.Empty(s.Box.Notes);
    }

    // ── Injuries ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnInjuredHumanHitter_MustBeReplacedBeforePlayContinues()
    {
        // 0.0 triggers the injury; the second value (0.0) then picks the first candidate: the batter.
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.0));
        var s = game.State!;
        var hurt = s.CurrentBatter;

        game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision());

        var notice = s.PendingInjury;
        Assert.NotNull(notice);
        Assert.Equal((true, hurt.Id, false), (notice!.Visitor, notice.PlayerId, notice.IsPitcher));
        Assert.Throws<InvalidOperationException>(() => game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision()));
        Assert.Null(game.ExecuteComputerTurn());

        var replacement = s.Bench(true).First();
        game.ResolveInjury(replacement.Id);

        Assert.Null(s.PendingInjury);
        Assert.DoesNotContain(hurt.Id, s.VisitorLineup);
        Assert.Contains(replacement.Id, s.VisitorLineup);
        Assert.Contains(hurt.Id, s.VisitorUsed);
        Assert.Contains(s.Box.Notes, n => n.Contains("injured"));
        Assert.Contains(s.PlayByPlay.Take(4), e => e.Description.Contains("replaces the injured"));
        game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision());   // and the game goes on
    }

    [Fact]
    public void AnInjuredRunnerOnBase_IsReplacedOnTheBasepaths()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.1), ManagerControl.Human);
        var s = game.State!;
        var runner = s.LineupPlayers(true).Skip(4).First().Player;
        s.RunnerOnFirst = new BaseRunner { Player = runner, ResponsiblePitcherId = s.HomePitcher.Player.Id };

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: s.CurrentBatter);

        Assert.NotNull(s.PendingInjury);
        var hurtId = s.PendingInjury!.PlayerId;
        game.ResolveInjury(s.Bench(true).First().Id);
        Assert.DoesNotContain(s.RunnerOnFirst?.Player.Id, new int?[] { hurtId });
        Assert.DoesNotContain(hurtId, s.VisitorLineup);
    }

    [Fact]
    public void AnInjuredPitcher_IsReplacedFromTheBullpen()
    {
        // Candidates in order: batter (1.0), pitcher (1 + pitches/60), then 8 fielders at 0.35. A roll of 0.3 lands on the pitcher.
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.3));
        var s = game.State!;
        var hurt = s.HomePitcher.Player;

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: s.CurrentBatter);   // home pitcher is computer-managed

        Assert.Null(s.PendingInjury);                         // the computer chose his replacement itself
        Assert.NotEqual(hurt.Id, s.HomePitcher.Player.Id);
        Assert.Contains(hurt.Id, s.HomeUsed);
        Assert.Equal(2, s.Box.Home.Pitchers.Count);
    }

    [Fact]
    public void ForAHumanPitcher_TheReliefChoiceIsLeftToTheManager()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.3), ManagerControl.Human, ManagerControl.Human);
        var s = game.State!;
        var hurt = s.HomePitcher.Player;

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: s.CurrentBatter);

        Assert.True(s.PendingInjury!.IsPitcher);
        var reliever = s.HomeBullpen.Last();
        game.ResolveInjury(reliever.Player.Id);
        Assert.Equal(reliever.Player.Id, s.HomePitcher.Player.Id);
        Assert.Contains(hurt.Id, s.HomeUsed);
    }

    [Fact]
    public void ComputerManagedTeams_ReplaceTheirHurtPlayersAutomatically_WithSomeoneWhoFits()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.0), ManagerControl.Computer);
        var s = game.State!;
        var hurt = s.CurrentBatter;
        var position = s.CurrentBatterPosition;

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: hurt);

        Assert.Null(s.PendingInjury);
        Assert.DoesNotContain(hurt.Id, s.VisitorLineup);
        Assert.Equal(9, s.VisitorLineup.Count);
        Assert.Equal(9, s.VisitorLineup.Distinct().Count());
        // The lineup stays legal: every position still filled exactly once.
        Assert.Empty(LineupRules.Validate(s.VisitorTeam, s.LineupPlayers(true).Select(x => new LineupSlot(x.Player.Id, x.Position)).ToList()));
        Assert.NotEqual(Position.DH, position);
    }

    [Fact]
    public void NobodyIsInjured_WhenThereIsNoOneToReplaceHim()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.0));
        var s = game.State!;
        foreach (var b in s.Bench(true).ToList()) s.VisitorUsed.Add(b.Id);
        foreach (var b in s.Bench(false).ToList()) s.HomeUsed.Add(b.Id);
        s.VisitorBullpen.Clear(); s.HomeBullpen.Clear();

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: s.CurrentBatter);

        Assert.Null(s.PendingInjury);
    }

    [Fact]
    public void InjuriesHappenOccasionallyInRealisticGames_AndEveryGameStillEnds()
    {
        int injuries = 0;
        // NOTE: a game called by rain is rare (a couple in a hundred), so the sample has to be big enough that a run
        // without one is unlikely whatever the random sequence happens to be (it changes whenever the manager's choices do).
        for (int seed = 0; seed < 400; seed++)
        {
            var rng = new Random(seed);
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng), rng);
            game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer,
                options: GameOptions.Standard);
            var s = game.State!;
            for (int plays = 0; s.Status == GameStatus.InProgress; plays++)
            {
                Assert.True(plays < 4000, $"seed {seed} never ended");
                game.ExecuteComputerTurn();
                Assert.Null(s.PendingInjury);   // computers never leave one waiting
            }
            injuries += s.Box.Notes.Count(n => n.Contains("injured"));
            Assert.Equal(9, s.VisitorLineup.Distinct().Count());
            Assert.Equal(9, s.HomeLineup.Distinct().Count());
        }

        Assert.InRange(injuries, 5, 60);   // roughly one in ten games
    }

    // ── Ejections ────────────────────────────────────────────────────────────

    [Fact]
    public void AHumanManagerEjectedAfterAStrikeout_HandsTheTeamToTheComputer()
    {
        var game = NewGame(new GameOptions(false, false, true), new ScriptedRandom(0.0));
        var s = game.State!;
        Assert.True(game.OffenseIsHuman);

        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1 }, wasTop: true);

        Assert.True(s.VisitorManagerEjected);
        Assert.Equal(ManagerControl.Computer, s.VisitorControl);
        Assert.False(game.OffenseIsHuman);
        Assert.Contains(s.PlayByPlay.Take(2), e => e.Description.Contains("ejected") && e.Description.Contains("computer takes over"));
        Assert.Contains(s.Box.Notes, n => n.Contains("manager ejected"));
    }

    [Fact]
    public void AWalkIsArguedByTheFieldingManager()
    {
        var game = NewGame(new GameOptions(false, false, true), new ScriptedRandom(0.0), ManagerControl.Computer, ManagerControl.Human);
        var s = game.State!;

        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Walk }, wasTop: true);   // visitors batting: home fields

        Assert.True(s.HomeManagerEjected);
        Assert.False(s.VisitorManagerEjected);
        Assert.Equal(ManagerControl.Computer, s.HomeControl);
    }

    [Fact]
    public void AComputerManagerCanBeEjectedToo_ButNothingChanges_AndNobodyIsEjectedTwice()
    {
        var game = NewGame(new GameOptions(false, false, true), new ScriptedRandom(0.0), ManagerControl.Computer);
        var s = game.State!;

        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Strikeout }, wasTop: true);
        int notes = s.Box.Notes.Count;
        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Strikeout }, wasTop: true);

        Assert.True(s.VisitorManagerEjected);
        Assert.Equal(ManagerControl.Computer, s.VisitorControl);
        Assert.Equal(notes, s.Box.Notes.Count);
    }

    [Fact]
    public void OnlyDisputablePlaysDrawEjections()
    {
        var game = NewGame(new GameOptions(false, false, true), new ScriptedRandom(0.0));
        var s = game.State!;

        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Single }, wasTop: true);
        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.HomeRun }, wasTop: true);
        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.GroundOut }, wasTop: true);

        Assert.False(s.VisitorManagerEjected || s.HomeManagerEjected);
    }

    // ── Rain ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ARainDelayEarlyInTheGame_CoolsTheArmsAndTheGameResumes()
    {
        var game = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        var s = game.State!;
        s.CurrentInning = 3; s.IsTopOfInning = true;
        s.VisitorPitcher.BattersFaced = 12; s.HomePitcher.BattersFaced = 12;
        var warm = s.HomeBullpen[0]; warm.IsWarming = true; warm.BattersFacedWarming = 6;

        game.MaybeRainDelay();

        Assert.Equal(GameStatus.InProgress, s.Status);
        Assert.Equal(1, s.RainDelays);
        Assert.False(warm.IsWarming);
        Assert.Equal(0, warm.BattersFacedWarming);
        Assert.Equal(RandomEvents.PostDelayModifier, s.HomePitcher.EntryModifier);
        Assert.Equal(12, s.HomePitcher.SettleFrom);   // he has to settle in again
        Assert.True(FatigueModel.GetModifier(s.HomePitcher) < 1.0);
        Assert.Contains(s.Box.Notes, n => n.Contains("Rain delay"));
    }

    [Fact]
    public void ARainDelayInAnOfficialGameWithALeader_CanEndTheGame()
    {
        var game = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        var s = game.State!;
        s.CurrentInning = 7; s.IsTopOfInning = true;
        s.VisitorScoreByInning[0] = 4;

        game.MaybeRainDelay();

        Assert.Equal(GameStatus.Final, s.Status);
        Assert.True(s.EndedByRain);
        Assert.Contains(s.Box.Notes, n => n.Contains("called by rain"));
        Assert.Contains("called, rain", BoxScoreFormatter.Result(s));
    }

    [Fact]
    public void AGameCalledAtTheStartOfATopHalf_DoesNotShowAnXForTheHomeTeam()
    {
        var game = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        var s = game.State!;
        s.CurrentInning = 7; s.IsTopOfInning = true;
        s.VisitorScoreByInning[0] = 4;

        game.MaybeRainDelay();

        Assert.True(s.EndedByRain);
        Assert.False(s.HomeDidNotBatLastInning);
        Assert.Equal("-", s.LineScoreCell(false, 7));
    }

    [Fact]
    public void ATiedGameIsNeverCalledForRain()
    {
        var game = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        var s = game.State!;
        s.CurrentInning = 7; s.IsTopOfInning = true;

        game.MaybeRainDelay();

        Assert.Equal(GameStatus.InProgress, s.Status);
        Assert.Equal(1, s.RainDelays);
    }

    [Fact]
    public void ANotYetOfficialGameIsNotCalled_ButFourAndAHalfWithTheHomeTeamAheadIs()
    {
        var early = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        early.State!.CurrentInning = 4; early.State.IsTopOfInning = true;
        early.State.VisitorScoreByInning[0] = 3;
        early.MaybeRainDelay();
        Assert.Equal(GameStatus.InProgress, early.State.Status);

        var half = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        half.State!.CurrentInning = 5; half.State.IsTopOfInning = false;   // 4½ innings played
        half.State.HomeScoreByInning[0] = 2;
        half.MaybeRainDelay();
        Assert.Equal(GameStatus.Final, half.State.Status);
    }

    [Fact]
    public void RainNeverInterruptsTheFirstInning_AndNeedsTheDiceToAgree()
    {
        var first = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        first.MaybeRainDelay();
        Assert.Equal(0, first.State!.RainDelays);

        var dry = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.999));
        dry.State!.CurrentInning = 6;
        dry.MaybeRainDelay();
        Assert.Equal(0, dry.State.RainDelays);
    }

    [Fact]
    public void RainOccursInSomeRealGames_AndANotInOthers()
    {
        int delayed = 0, called = 0;
        // NOTE: a game called by rain is rare (a couple in a hundred), so the sample has to be big enough that a run
        // without one is unlikely whatever the random sequence happens to be (it changes whenever the manager's choices do).
        for (int seed = 0; seed < 400; seed++)
        {
            var rng = new Random(seed);
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng), rng);
            game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer,
                options: GameOptions.Standard);
            var s = game.State!;
            while (s.Status == GameStatus.InProgress) game.ExecuteComputerTurn();
            if (s.RainDelays > 0) delayed++;
            if (s.EndedByRain)
            {
                called++;
                Assert.NotEqual(s.VisitorTotalScore, s.HomeTotalScore);   // never called while tied
            }
        }
        Assert.InRange(delayed, 4, 130);
        Assert.True(called >= 1);
    }

    // ── Saving a game with events in it ──────────────────────────────────────

    [Fact]
    public void ASavedGame_KeepsItsBoxScoreEventStateAndPendingInjury()
    {
        var game = NewGame(new GameOptions(true, true, true), new ScriptedRandom(0.0, 0.0));
        var s = game.State!;
        s.Stadium = new Stadium { Id = "test", Name = "Test Park" };
        game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision());   // injury strikes the batter
        s.RainDelays = 2;
        s.VisitorManagerEjected = true;
        s.ErrorThisHalfInning = true;
        Assert.NotNull(s.PendingInjury);

        var json = GameSnapshots.ToJson(GameSnapshots.Capture(s));
        var snap = GameSnapshots.FromJson(json)!;
        var restored = new GameService(new AtBatResolver(), new ComputerManager());
        var visitor = s.VisitorTeam; var home = s.HomeTeam;
        restored.Resume(snap, id => id == visitor.Id ? visitor : home, id => new Stadium { Id = id, Name = "Test Park" });
        var r = restored.State!;

        Assert.Equal(s.PendingInjury, r.PendingInjury);
        Assert.Equal(2, r.RainDelays);
        Assert.True(r.VisitorManagerEjected && r.ErrorThisHalfInning);
        Assert.Equal(GameOptions.Standard, r.Options);
        Assert.Equal("Test Park", r.Stadium.Name);
        Assert.Equal(s.Box.Visitor.Batters.Select(b => (b.Name, b.AB, b.Pos)), r.Box.Visitor.Batters.Select(b => (b.Name, b.AB, b.Pos)));
        Assert.Equal(s.Box.Notes, r.Box.Notes);
        Assert.Equal(BoxScoreFormatter.ToText(s), BoxScoreFormatter.ToText(r));

        restored.ResolveInjury(r.Bench(true).First().Id);   // a resumed game can finish the replacement
        Assert.Null(r.PendingInjury);
    }
}
