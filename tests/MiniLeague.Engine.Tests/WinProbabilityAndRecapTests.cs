using System.Diagnostics;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The win-probability model and the recap and highlights built from it.</summary>
public class WinProbabilityAndRecapTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static double Home(int inning, bool top, int outs, int bases, int margin) =>
        WinProbability.HomeWins(inning, top, outs, bases, margin);

    // ── The model ────────────────────────────────────────────────────────────

    [Fact]
    public void AGameJustStarting_IsAboutEven_WithASmallEdgeForTheTeamBattingLast()
    {
        double p = Home(1, top: true, outs: 0, bases: 0, margin: 0);

        Assert.InRange(p, 0.49, 0.57);
    }

    [Fact]
    public void ALeadAlwaysHelps_AndABiggerLeadHelpsMore()
    {
        foreach (int inning in new[] { 1, 5, 9 })
        {
            double previous = -1;
            for (int margin = -6; margin <= 6; margin++)
            {
                double p = Home(inning, top: true, outs: 0, bases: 0, margin);
                Assert.InRange(p, 0.0, 1.0);
                Assert.True(p > previous, $"inning {inning}, margin {margin}");
                previous = p;
            }
        }
    }

    [Fact]
    public void TheSameLead_MeansMoreAsTheGameGoesOn()
    {
        double early = Home(2, top: true, 0, 0, margin: 2);
        double middle = Home(6, top: true, 0, 0, margin: 2);
        double late = Home(9, top: true, 0, 0, margin: 2);

        Assert.True(early < middle && middle < late);
        Assert.InRange(early, 0.6, 0.78);
        Assert.InRange(late, 0.85, 0.97);
    }

    [Fact]
    public void LateGameSituationsLookLikeRealBaseball()
    {
        // the home team tied in the bottom of the ninth with nobody on: good, not certain
        Assert.InRange(Home(9, top: false, 0, 0, 0), 0.58, 0.75);
        // trailing by three in the bottom of the ninth with the bases empty: nearly hopeless
        Assert.InRange(Home(9, top: false, 0, 0, -3), 0.01, 0.10);
        // down one with a runner on second and nobody out: about even
        Assert.InRange(Home(9, top: false, 0, 2, -1), 0.40, 0.65);
        // up by eight in the top of the ninth
        Assert.True(Home(9, top: true, 0, 0, 8) > 0.99);
        // down by eight in the bottom of the ninth
        Assert.True(Home(9, top: false, 2, 0, -8) < 0.001);
    }

    [Fact]
    public void RunnersAndOuts_MoveTheOddsTheRightWay()
    {
        // the home team is batting in the bottom of the 5th, tied
        double empty2 = Home(5, top: false, 2, 0, 0);
        double empty0 = Home(5, top: false, 0, 0, 0);
        double loaded0 = Home(5, top: false, 0, 7, 0);
        Assert.True(empty2 < empty0 && empty0 < loaded0);

        // and for the visitors batting in the top, the same situation helps the visitors (hurts the home team)
        Assert.True(Home(5, top: true, 0, 7, 0) < Home(5, top: true, 0, 0, 0));
        Assert.True(Home(5, top: true, 2, 0, 0) > Home(5, top: true, 0, 0, 0));
    }

    [Fact]
    public void ExtraInnings_AreHandled_AndTheTeamBattingLastHasTheEdge()
    {
        double tied10 = Home(10, top: false, 0, 0, 0);
        double tied12top = Home(12, top: true, 0, 0, 0);

        Assert.InRange(tied10, 0.55, 0.80);
        Assert.InRange(tied12top, 0.45, 0.62);
        Assert.InRange(Home(14, top: false, 0, 0, 1), 0.5, 1.0);
    }

    [Fact]
    public void AFinishedGameIsCertain()
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)), new Random(1));
        game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer);
        while (game.State!.Status == GameStatus.InProgress) game.ExecuteComputerTurn();

        double expected = game.State.HomeTotalScore > game.State.VisitorTotalScore ? 1.0 : 0.0;
        Assert.Equal(expected, game.State.HomeWinProbability);
    }

    [Fact]
    public void TheRunExpectancyTableIsSane()
    {
        for (int bases = 0; bases < 8; bases++)
            Assert.True(WinProbability.ExpectedRuns(bases, 0) > WinProbability.ExpectedRuns(bases, 1)
                        && WinProbability.ExpectedRuns(bases, 1) > WinProbability.ExpectedRuns(bases, 2));
        Assert.True(WinProbability.ExpectedRuns(7, 0) > WinProbability.ExpectedRuns(0, 0) * 4);
    }

    [Fact]
    public void ItIsFastEnoughToRunAfterEveryPlay()
    {
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 3000; i++)
            Home(1 + i % 12, i % 2 == 0, i % 3, i % 8, (i % 9) - 4);

        Assert.True(clock.ElapsedMilliseconds < 2500, $"{clock.ElapsedMilliseconds} ms for 3000 evaluations");
    }

    // ── Recording the track ──────────────────────────────────────────────────

    private static GameService NewGame(int seed = 1)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer);
        return game;
    }

    private static PlayResult Out(string text = "grounds out.") => new()
    { Outcome = OutcomeType.GroundOut, OutsRecorded = 1, Description = text, PitchesThrown = 3 };

    private static PlayResult Homer(int runs, string text) => new()
    { Outcome = OutcomeType.HomeRun, RunsScored = runs, Description = text, PitchesThrown = 3 };

    [Fact]
    public void EveryPlayIsOnTheTrack_WithTheSwingItCaused()
    {
        var game = NewGame();
        var s = game.State!;
        Assert.Empty(s.Recap);
        Assert.InRange(s.LastHomeWinProbability, 0.49, 0.57);
        double start = s.LastHomeWinProbability;

        game.ApplyPlay(Homer(2, "Ruth homers!"));
        game.ApplyPlay(Out());

        Assert.Equal(2, s.Recap.Count);
        var first = s.Recap[0];
        Assert.Equal((1, true, 2, 0), (first.Inning, first.IsTop, first.VisitorScore, first.HomeScore));
        Assert.True(first.Swing < -0.05, "a two-run homer by the visitors hurts the home team");
        Assert.Equal(first.HomeWinProbability - start, first.Swing, 9);
        Assert.Equal(s.Recap[1].HomeWinProbability - first.HomeWinProbability, s.Recap[1].Swing, 9);
        Assert.Equal(s.Recap[^1].HomeWinProbability, s.LastHomeWinProbability);
    }

    [Fact]
    public void TheSwingsAddUpToTheWholeGame_AndAGameEndsAtCertainty()
    {
        var game = NewGame(5);
        var s = game.State!;
        double start = s.LastHomeWinProbability;
        while (s.Status == GameStatus.InProgress) game.ExecuteComputerTurn();

        Assert.Equal(s.Recap[^1].HomeWinProbability - start, s.Recap.Sum(p => p.Swing), 9);
        Assert.Contains(s.Recap[^1].HomeWinProbability, new[] { 0.0, 1.0 });
        Assert.All(s.Recap, p => Assert.InRange(p.HomeWinProbability, 0.0, 1.0));
    }

    // ── The recap ────────────────────────────────────────────────────────────

    // Plays a scripted game: the visitors lead early, the home team comes back and wins on a walk-off.
    private static GameState ScriptedComeback()
    {
        var game = NewGame();
        var s = game.State!;

        game.ApplyPlay(Homer(3, "B. Ruth homers! 3 runs score."));           // ▲1: visitors lead 3-0
        for (int i = 0; i < 3; i++) game.ApplyPlay(Out());
        for (int i = 0; i < 3; i++) game.ApplyPlay(Out());                    // ▼1: no runs
        s.CurrentInning = 6; s.IsTopOfInning = false; s.Outs = 0;
        game.ApplyPlay(Homer(3, "C. Yastrzemski homers! 3 runs score."));     // ▼6: tied 3-3
        s.CurrentInning = 9; s.IsTopOfInning = false; s.Outs = 1;
        game.ApplyPlay(Homer(1, "J. Rice homers! 1 run scores."));            // ▼9: walk-off, 4-3
        return s;
    }

    [Fact]
    public void TheHeadline_NamesTheWinnerAndTheScore()
    {
        var s = ScriptedComeback();

        var recap = GameRecaps.Build(s);

        Assert.Equal(GameStatus.Final, s.Status);
        Assert.StartsWith("1975 Boston Red Sox beat the 1927 New York Yankees 4–3", recap.Headline);
    }

    [Fact]
    public void TheStory_MentionsTheComebackTheWalkOffAndTheBiggestPlay()
    {
        var s = ScriptedComeback();

        var story = GameRecaps.Build(s).Story;

        Assert.Contains(story, l => l.Contains("BOS came back from 3 runs down"));
        Assert.Contains(story, l => l.Contains("walk-off") && l.Contains("J. Rice homers"));
        Assert.Contains(story, l => l.StartsWith("The biggest play:"));
    }

    [Fact]
    public void TheHighlights_AreTheBiggestSwings_InTheOrderTheyHappened()
    {
        var s = ScriptedComeback();

        var recap = GameRecaps.Build(s);

        Assert.InRange(recap.KeyPlays.Count, 2, GameRecaps.KeyPlayCount);
        var order = recap.KeyPlays.Select(p => recap.Track.ToList().IndexOf(p)).ToList();
        Assert.Equal(order.Order(), order);                                     // chronological
        Assert.Contains(recap.KeyPlays, p => p.Description.Contains("Ruth"));
        Assert.Contains(recap.KeyPlays, p => p.Description.Contains("Rice"));
        // nothing that barely moved the game is a highlight
        Assert.All(recap.KeyPlays, p => Assert.True(Math.Abs(p.Swing) >= 0.04));
        var biggest = recap.Track.OrderByDescending(p => Math.Abs(p.Swing)).First();
        Assert.Contains(biggest, recap.KeyPlays);
    }

    [Fact]
    public void TheRecapNamesTheStandoutHitters()
    {
        var s = ScriptedComeback();

        var recap = GameRecaps.Build(s);

        Assert.NotEmpty(recap.Performers);
        Assert.True(recap.Performers.Count <= 3);
        Assert.Contains(recap.Performers, p => p.Contains("HR"));
    }

    [Fact]
    public void ARecapWorksForAGameInProgress()
    {
        var game = NewGame();
        game.ApplyPlay(Homer(1, "Combs homers!"));

        var recap = GameRecaps.Build(game.State!);

        Assert.StartsWith("NYY 1, BOS 0", recap.Headline);
        Assert.Contains("top of the 1st", recap.Headline);
        Assert.Single(recap.Track);
    }

    [Fact]
    public void AnEmptyGameHasNoStoryButStillARecap()
    {
        var recap = GameRecaps.Build(NewGame().State!);

        Assert.Empty(recap.Track);
        Assert.Empty(recap.KeyPlays);
        Assert.Empty(recap.Story);
        Assert.NotEmpty(recap.Headline);
    }

    [Fact]
    public void ARoutIsDescribedAsWireToWire()
    {
        var game = NewGame();
        var s = game.State!;
        game.ApplyPlay(Homer(2, "Ruth homers!"));
        s.CurrentInning = 9; s.IsTopOfInning = false; s.Outs = 2;
        game.ApplyPlay(Out());                                                // game over, 2-0

        var story = GameRecaps.Build(s).Story;

        Assert.Contains(story, l => l.Contains("NYY led from the first run to the last"));
    }

    [Fact]
    public void ARealGamesRecap_IsConsistentWithItsBoxScore()
    {
        for (int seed = 0; seed < 25; seed++)
        {
            var game = NewGame(seed);
            var s = game.State!;
            while (s.Status == GameStatus.InProgress) game.ExecuteComputerTurn();

            var recap = GameRecaps.Build(s);

            Assert.Equal(s.VisitorTotalScore, recap.Track[^1].VisitorScore);
            Assert.Equal(s.HomeTotalScore, recap.Track[^1].HomeScore);
            Assert.True(recap.KeyPlays.Count <= GameRecaps.KeyPlayCount);
            Assert.StartsWith(s.HomeTotalScore > s.VisitorTotalScore ? s.HomeTeam.DisplayName : s.VisitorTeam.DisplayName,
                recap.Headline);
            Assert.Contains(recap.Story, l => l.StartsWith("Winning pitcher:"));
        }
    }

    [Fact]
    public void TheTextRecap_ReadsLikeAShareableSummary()
    {
        var s = ScriptedComeback();

        string text = GameRecaps.ToText(s);

        Assert.Contains("1975 Boston Red Sox beat the 1927 New York Yankees 4–3", text);
        Assert.Contains("Top performers", text);
        Assert.Contains("Highlights", text);
        Assert.Contains("J. Rice homers", text);
        Assert.Contains("BOS", text);
        Assert.Matches(@"\(\+\d+% BOS\)", text);   // the swing, credited to the team it helped
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheTrackSurvivesSavingAndResuming_AndTheGameKeepsRecording()
    {
        var game = NewGame(3);
        var s = game.State!;
        for (int i = 0; i < 20; i++) game.ExecuteComputerTurn();

        var snapshot = GameSnapshots.FromJson(GameSnapshots.ToJson(GameSnapshots.Capture(s)))!;
        var resumed = new GameService(new AtBatResolver(new Random(9)), new ComputerManager(new Random(9)), new Random(9));
        var visitor = s.VisitorTeam; var home = s.HomeTeam;
        resumed.Resume(snapshot, id => id == visitor.Id ? visitor : home);
        var r = resumed.State!;

        Assert.Equal(s.Recap.Select(p => (p.Description, p.HomeWinProbability)), r.Recap.Select(p => (p.Description, p.HomeWinProbability)));
        Assert.Equal(s.LastHomeWinProbability, r.LastHomeWinProbability, 9);

        resumed.ExecuteComputerTurn();
        Assert.Equal(s.Recap.Count + 1, r.Recap.Count);
        Assert.Equal(r.Recap[^1].HomeWinProbability - s.Recap[^1].HomeWinProbability, r.Recap[^1].Swing, 9);
    }

    [Fact]
    public void AnOlderSaveWithoutATrack_StillResumes()
    {
        var game = NewGame();
        game.ExecuteComputerTurn();
        var snapshot = GameSnapshots.Capture(game.State!);
        snapshot.Recap.Clear();
        var visitor = game.State!.VisitorTeam; var home = game.State.HomeTeam;
        var resumed = new GameService(new AtBatResolver(), new ComputerManager());

        resumed.Resume(snapshot, id => id == visitor.Id ? visitor : home);

        Assert.Empty(resumed.State!.Recap);
        Assert.Equal(0.5, resumed.State.LastHomeWinProbability);
        resumed.ExecuteComputerTurn();   // and recording picks up from here
        Assert.Single(resumed.State.Recap);
    }
}
