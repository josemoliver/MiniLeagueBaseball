using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>Per-game stat tracking: who gets credit for what, decisions, and the printed box score.</summary>
public class BoxScoreTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    // Visitors (1927 Yankees) bat in the top of the first; the home team is the 1975 Red Sox.
    private static GameService NewGame()
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)), new Random(1));
        game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer);
        return game;
    }

    private static BaseRunner Runner(Player p, int pitcherId = 0) =>
        new() { Player = p, ResponsiblePitcherId = pitcherId };

    private static Player Batter(GameState s, int slot) =>
        s.TeamFor(true).Roster.First(p => p.Id == s.VisitorLineup[slot]);

    private static BatterLine Line(GameState s, Player p) => s.Box.Visitor.Batters.First(b => b.PlayerId == p.Id);

    // ── Starting lines ───────────────────────────────────────────────────────

    [Fact]
    public void TheBoxScoreStartsWithEveryStarterAndBothStartingPitchers()
    {
        var s = NewGame().State!;

        Assert.Equal(9, s.Box.Visitor.Batters.Count);
        Assert.Equal(9, s.Box.Home.Batters.Count);
        Assert.Equal(s.VisitorPitcher.Player.Id, Assert.Single(s.Box.Visitor.Pitchers).PlayerId);
        Assert.Equal(s.VisitorLineup, s.Box.Visitor.Batters.OrderBy(b => b.Slot).Select(b => b.PlayerId));
        Assert.All(s.Box.Visitor.Batters, b => Assert.NotEmpty(b.Pos));
    }

    // ── Crediting plays ──────────────────────────────────────────────────────

    [Fact]
    public void ASingleDrivingInTwo_CreditsHitRbiRunsAndThePitcher()
    {
        var game = NewGame();
        var s = game.State!;
        var batter = s.CurrentBatter;
        var onThird = Batter(s, 5); var onSecond = Batter(s, 6);
        s.RunnerOnThird = Runner(onThird, s.HomePitcher.Player.Id);
        s.RunnerOnSecond = Runner(onSecond, s.HomePitcher.Player.Id);

        game.ApplyResult(new PlayResult
        {
            Outcome = OutcomeType.Single, RunsScored = 2, PitchesThrown = 4,
            NewFirst = Runner(batter),
        });

        var b = Line(s, batter);
        Assert.Equal((1, 1, 2), (b.AB, b.H, b.RBI));
        Assert.Equal(1, Line(s, onThird).R);
        Assert.Equal(1, Line(s, onSecond).R);
        var p = s.Box.Home.Pitchers[0];
        Assert.Equal((1, 2, 2, 1, 4), (p.H, p.R, p.ER, p.BF, p.Pitches));
    }

    [Fact]
    public void AHomeRun_CreditsTheBatterAndEveryRunnerWhoScored()
    {
        var game = NewGame();
        var s = game.State!;
        var batter = s.CurrentBatter;
        var onFirst = Batter(s, 4);
        s.RunnerOnFirst = Runner(onFirst, s.HomePitcher.Player.Id);

        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 2, PitchesThrown = 3 });

        var b = Line(s, batter);
        Assert.Equal((1, 1, 1, 2, 1), (b.AB, b.H, b.HR, b.RBI, b.R));
        Assert.Equal(1, Line(s, onFirst).R);
        Assert.Equal(1, s.Box.Home.Pitchers[0].HR);
    }

    [Fact]
    public void AnError_IsAnAtBatWithNoHitNoRbi_ChargesTheFielding_AndTheRunIsUnearned()
    {
        var game = NewGame();
        var s = game.State!;
        var batter = s.CurrentBatter;
        var onThird = Batter(s, 5);
        s.RunnerOnThird = Runner(onThird, s.HomePitcher.Player.Id);
        var shortstop = s.DefenderAt(Position.Short)!;

        game.ApplyResult(new PlayResult
        {
            Outcome = OutcomeType.Error, RunsScored = 1, NewFirst = Runner(batter),
            ErrorBy = shortstop, ErrorPosition = Position.Short, PitchesThrown = 2,
        });

        var b = Line(s, batter);
        Assert.Equal((1, 0, 0), (b.AB, b.H, b.RBI));
        Assert.Equal(1, Line(s, onThird).R);
        Assert.Equal(1, s.Box.Home.Errors);
        Assert.Contains("SS", s.Box.Home.ErrorNotes[0]);
        var p = s.Box.Home.Pitchers[0];
        Assert.Equal((1, 0), (p.R, p.ER));
    }

    [Fact]
    public void RunsAfterAnErrorInTheSameInningAreUnearned_ButNotTheNextInning()
    {
        var game = NewGame();
        var s = game.State!;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.Error, NewFirst = Runner(s.CurrentBatter), ErrorPosition = Position.Short });

        var later = s.CurrentBatter;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 2 });
        var pitcher = s.Box.Home.Pitchers[0];
        Assert.Equal((2, 0), (pitcher.R, pitcher.ER));   // both runs unearned: the inning had an error

        game.ApplyResult(new PlayResult { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 });
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 });
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 });   // side retired
        Assert.False(s.IsTopOfInning);
        s.IsTopOfInning = true; s.CurrentInning = 2;   // a fresh half-inning
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 1 });
        Assert.Equal((3, 1), (pitcher.R, pitcher.ER));
        Assert.NotNull(later);
    }

    [Fact]
    public void WalksAndStrikeouts_AreCountedWithoutAnAtBatForTheWalk()
    {
        var game = NewGame();
        var s = game.State!;
        var first = s.CurrentBatter;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.Walk, NewFirst = Runner(first), PitchesThrown = 6 });
        var second = s.CurrentBatter;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1, PitchesThrown = 5, NewFirst = Runner(first) });

        Assert.Equal((0, 1), (Line(s, first).AB, Line(s, first).BB));
        Assert.Equal((1, 1), (Line(s, second).AB, Line(s, second).SO));
        var p = s.Box.Home.Pitchers[0];
        Assert.Equal((1, 1, 2, 11, 1), (p.BB, p.SO, p.BF, p.Pitches, p.Outs));
    }

    [Fact]
    public void ABasesLoadedWalk_ScoresARunWithAnRbi()
    {
        var game = NewGame();
        var s = game.State!;
        var batter = s.CurrentBatter;
        s.RunnerOnFirst = Runner(Batter(s, 3), s.HomePitcher.Player.Id);
        s.RunnerOnSecond = Runner(Batter(s, 4), s.HomePitcher.Player.Id);
        var third = Batter(s, 5);
        s.RunnerOnThird = Runner(third, s.HomePitcher.Player.Id);

        game.ApplyResult(new PlayResult
        {
            Outcome = OutcomeType.Walk, RunsScored = 1,
            NewFirst = Runner(batter), NewSecond = s.RunnerOnFirst, NewThird = s.RunnerOnSecond,
        });

        Assert.Equal(1, Line(s, batter).RBI);
        Assert.Equal(1, Line(s, third).R);
    }

    [Fact]
    public void StolenBasesAndCaughtStealing_AreCreditedToTheRunner()
    {
        var game = NewGame();
        var s = game.State!;
        var runner = Batter(s, 3);
        s.RunnerOnFirst = Runner(runner, s.HomePitcher.Player.Id);

        game.ApplyResult(new PlayResult { Outcome = OutcomeType.StolenBase, NewSecond = s.RunnerOnFirst });
        s.RunnerOnFirst = Runner(runner);
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.CaughtStealing, OutsRecorded = 1 });

        Assert.Equal((1, 1), (Line(s, runner).SB, Line(s, runner).CS));
        Assert.Equal(0, s.Box.Home.Pitchers[0].BF);   // a steal attempt faces no batter
    }

    [Fact]
    public void ADoublePlay_IsCountedForTheFieldingTeam()
    {
        var game = NewGame();
        var s = game.State!;
        s.RunnerOnFirst = Runner(Batter(s, 3), s.HomePitcher.Player.Id);

        game.ApplyResult(new PlayResult { Outcome = OutcomeType.DoublePlay, OutsRecorded = 2 });

        Assert.Equal(1, s.Box.Home.DoublePlays);
        Assert.Equal(2, s.Box.Home.Pitchers[0].Outs);
    }

    [Fact]
    public void RunnersLeftOnBase_AreCountedWhenTheInningEnds()
    {
        var game = NewGame();
        var s = game.State!;
        s.Outs = 2;
        s.RunnerOnFirst = Runner(Batter(s, 3), s.HomePitcher.Player.Id);
        s.RunnerOnThird = Runner(Batter(s, 4), s.HomePitcher.Player.Id);

        game.ApplyResult(new PlayResult
        {
            Outcome = OutcomeType.FlyOut, OutsRecorded = 1, NewFirst = s.RunnerOnFirst, NewThird = s.RunnerOnThird,
        });

        Assert.Equal(2, s.Box.Visitor.LeftOnBase);
        Assert.Equal(0, s.Box.Home.LeftOnBase);
    }

    // ── Pitchers ─────────────────────────────────────────────────────────────

    [Fact]
    public void ARunnerWhoReachedAgainstTheStarter_IsChargedToTheStarterEvenIfARelieverGivesUpTheHit()
    {
        var game = NewGame();
        var s = game.State!;
        var starter = s.HomePitcher.Player;
        var runner = Batter(s, 3);
        s.RunnerOnFirst = Runner(runner, starter.Id);
        var reliever = s.HomeBullpen.First();
        game.ChangePitcher(false, reliever.Player.Id);

        var batter = s.CurrentBatter;
        game.ApplyResult(new PlayResult
        {
            Outcome = OutcomeType.Double, RunsScored = 1, NewSecond = Runner(batter), PitchesThrown = 3,
        });

        var starterLine = s.Box.Home.Pitchers.First(p => p.PlayerId == starter.Id);
        var relieverLine = s.Box.Home.Pitchers.First(p => p.PlayerId == reliever.Player.Id);
        Assert.Equal(1, starterLine.R);    // the inherited runner is the starter's
        Assert.Equal(0, relieverLine.R);
        Assert.Equal(1, relieverLine.H);   // but the hit is the reliever's
    }

    [Fact]
    public void ABatterWhoReachesAgainstAPitcherIsChargedToHim_WhenHeScoresLater()
    {
        var game = NewGame();
        var s = game.State!;
        var starter = s.HomePitcher.Player;
        var batter = s.CurrentBatter;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.Single, NewFirst = Runner(batter) });
        Assert.Equal(starter.Id, s.RunnerOnFirst!.ResponsiblePitcherId);   // stamped when he reached

        game.ChangePitcher(false, s.HomeBullpen.First().Player.Id);
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 2 });

        var starterLine = s.Box.Home.Pitchers.First(p => p.PlayerId == starter.Id);
        Assert.Equal(1, starterLine.R);
    }

    [Fact]
    public void InningsPitched_AreWrittenInThirds()
    {
        Assert.Equal("6.2", new PitcherLine { Outs = 20 }.IP);
        Assert.Equal("0.1", new PitcherLine { Outs = 1 }.IP);
        Assert.Equal("9.0", new PitcherLine { Outs = 27 }.IP);
    }

    // ── Substitutes ──────────────────────────────────────────────────────────

    [Fact]
    public void SubstitutesAppearUnderTheSameBattingSpotWithPhAndPrNotation()
    {
        var game = NewGame();
        var s = game.State!;
        var replaced = s.CurrentBatter;
        int slot = s.VisitorLineup.IndexOf(replaced.Id);
        var pinch = s.Bench(true).First();

        game.PinchHit(pinch.Id);

        var line = Line(s, pinch);
        Assert.Equal(slot, line.Slot);
        Assert.StartsWith("PH-", line.Pos);
        Assert.Equal(10, s.Box.Visitor.Batters.Count);

        s.RunnerOnFirst = Runner(Batter(s, 5), s.HomePitcher.Player.Id);
        var runnerSub = s.Bench(true).First();
        game.PinchRun(1, runnerSub.Id);
        Assert.StartsWith("PR-", Line(s, runnerSub).Pos);
    }

    // ── Decisions ────────────────────────────────────────────────────────────

    // Plays out a finished game where the visitors take the lead in the first and hold it.
    private static GameState FinishedGame(Action<GameState> beforeEnd)
    {
        var game = NewGame();
        var s = game.State!;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 1 });   // visitors lead 1-0
        beforeEnd(s);
        s.CurrentInning = 9; s.IsTopOfInning = false; s.Outs = 2;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 });   // game over
        Assert.Equal(GameStatus.Final, s.Status);
        return s;
    }

    [Fact]
    public void AStarterWhoGoesFiveInnings_GetsTheWin_AndTheOtherStarterTheLoss()
    {
        var s = FinishedGame(g => g.Box.Visitor.Pitchers[0].Outs = 27);

        Assert.Equal("W", s.Box.Visitor.Pitchers[0].Decision);
        Assert.Equal("L", s.Box.Home.Pitchers[0].Decision);
        Assert.DoesNotContain(s.Box.Visitor.Pitchers, p => p.Decision == "S");
    }

    [Fact]
    public void AStarterWhoFallsShortOfFiveInnings_HandsTheWinToTheBestReliever()
    {
        var game = NewGame();
        var s = game.State!;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 1 });
        s.Box.Visitor.Pitchers[0].Outs = 12;                       // 4 innings
        s.IsTopOfInning = false;
        game.ChangePitcher(true, s.VisitorBullpen[0].Player.Id);   // long man
        var longMan = s.Box.Visitor.Pitchers[1]; longMan.Outs = 12;
        game.ChangePitcher(true, s.VisitorBullpen[0].Player.Id);   // closer finishes
        var finisher = s.Box.Visitor.Pitchers[2]; finisher.Outs = 3;

        s.CurrentInning = 9; s.Outs = 2;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 });

        Assert.Equal("", s.Box.Visitor.Pitchers[0].Decision);
        Assert.Equal("W", longMan.Decision);
        Assert.Equal("S", finisher.Decision);   // entered with a one-run lead and finished
    }

    [Fact]
    public void ALateGoAheadRun_PutsTheWinOnThePitcherOfRecordAtThatMoment()
    {
        var game = NewGame();
        var s = game.State!;
        s.Box.Visitor.Pitchers[0].Outs = 18;
        // Home leads 1-0 after the first (home bats in the bottom).
        s.IsTopOfInning = false;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 1 });
        Assert.NotNull(s.Box.GoAhead);
        Assert.False(s.Box.GoAhead!.VisitorLead);

        // The visitors tie it, then take the lead with a reliever on the mound for them.
        s.IsTopOfInning = true;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 1 });
        Assert.Null(s.Box.GoAhead);   // tied: nobody is ahead
        game.ChangePitcher(false, s.HomeBullpen[0].Player.Id);   // home brings in a reliever
        var homeReliever = s.HomePitcher.Player;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.HomeRun, RunsScored = 1 });

        Assert.True(s.Box.GoAhead!.VisitorLead);
        Assert.Equal(homeReliever.Id, s.Box.GoAhead.LoserPitcherId);   // he gave up the lead
    }

    [Fact]
    public void ATiedGameCalledByRain_HasNoDecisions()
    {
        var game = NewGame();
        var s = game.State!;
        s.EndedByRain = true;
        s.CurrentInning = 6;
        s.IsTopOfInning = false;
        s.Outs = 2;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 });
        BoxScoreRecorder.Finish(s);

        Assert.All(s.Box.Visitor.Pitchers.Concat(s.Box.Home.Pitchers), p => Assert.Equal("", p.Decision));
    }

    // ── The printed box score ────────────────────────────────────────────────

    [Fact]
    public void TheTextBoxScore_ListsTeamsLineScorePlayersPitchersAndNotes()
    {
        var s = FinishedGame(g => g.Box.Visitor.Pitchers[0].Outs = 27);
        s.Box.Notes.Add("BOS: someone was injured.");

        string text = BoxScoreFormatter.ToText(s);

        Assert.Contains("1927 New York Yankees 1, 1975 Boston Red Sox 0", text);
        Assert.Contains("NYY", text);
        Assert.Contains("AB  R  H RBI BB SO", text);
        Assert.Contains("Totals", text);
        Assert.Contains("(W)", text);
        Assert.Contains("(L)", text);
        Assert.Contains("HR:", text);
        Assert.Contains("LOB:", text);
        Assert.Contains("9.1", text);
        Assert.Contains("BOS: someone was injured.", text);
        Assert.Contains("Fenway", BoxScoreFormatter.ToText(StadiumGame()));
    }

    private static GameState StadiumGame()
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)));
        game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer,
            stadium: new Stadium { Id = "f", Name = "Fenway Park" });
        return game.State!;
    }

    [Fact]
    public void TheTextBoxScore_ShowsXWhenTheHomeTeamDidNotBat_AndExtraInningColumns()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentInning = 9; s.IsTopOfInning = true; s.Outs = 2;
        s.HomeScoreByInning[2] = 3;
        game.ApplyResult(new PlayResult { Outcome = OutcomeType.GroundOut, OutsRecorded = 1 });
        Assert.True(s.HomeDidNotBatLastInning);
        Assert.Contains("X", BoxScoreFormatter.ToText(s).Split('\n')[5]);   // BOS line of the line score

        var extra = NewGame().State!;
        extra.CurrentInning = 11;
        string header = BoxScoreFormatter.ToText(extra).Split('\n')[3];
        Assert.Contains("11", header);
    }

    // ── Whole games ──────────────────────────────────────────────────────────

    [Fact]
    public void ForWholeGames_TheBoxScoreAddsUp()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var rng = new Random(seed);
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng), rng);
            game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer);
            var s = game.State!;
            while (s.Status == GameStatus.InProgress) game.ExecuteComputerTurn();

            foreach (bool visitor in new[] { true, false })
            {
                var box = s.Box.For(visitor);
                var opp = s.Box.For(!visitor);
                int total = visitor ? s.VisitorTotalScore : s.HomeTotalScore;

                Assert.Equal(total, box.Batters.Sum(b => b.R));                          // runs scored = runs on the board
                Assert.Equal(total, opp.Pitchers.Sum(p => p.R));                         // ...and runs charged to pitchers
                Assert.True(opp.Pitchers.Sum(p => p.ER) <= total);
                Assert.Equal(box.Batters.Sum(b => b.H), opp.Pitchers.Sum(p => p.H));     // hits batted = hits allowed
                Assert.Equal(box.Batters.Sum(b => b.BB), opp.Pitchers.Sum(p => p.BB));
                Assert.Equal(box.Batters.Sum(b => b.SO), opp.Pitchers.Sum(p => p.SO));
                Assert.Equal(box.Batters.Sum(b => b.HR), opp.Pitchers.Sum(p => p.HR));
                Assert.True(box.Batters.Sum(b => b.RBI) <= total);
                Assert.Equal(box.Batters.Sum(b => b.AB) + box.Batters.Sum(b => b.BB) + box.Batters.Sum(b => b.HBP) + box.Batters.Sum(b => b.SH),
                    opp.Pitchers.Sum(p => p.BF));                                        // every plate appearance is a batter faced
            }

            // The winner gets exactly one W and the loser one L (rain-free, so the game has a leader).
            var winners = s.Box.Visitor.Pitchers.Concat(s.Box.Home.Pitchers).Count(p => p.Decision == "W");
            var losers = s.Box.Visitor.Pitchers.Concat(s.Box.Home.Pitchers).Count(p => p.Decision == "L");
            Assert.Equal((1, 1), (winners, losers));
            bool visitorWon = s.VisitorTotalScore > s.HomeTotalScore;
            Assert.Contains(s.Box.For(visitorWon).Pitchers, p => p.Decision == "W");
            Assert.Contains(s.Box.For(!visitorWon).Pitchers, p => p.Decision == "L");
        }
    }
}
