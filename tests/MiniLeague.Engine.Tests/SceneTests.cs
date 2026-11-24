using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The scenes around the plays: the introduction, new half-innings, substitutions, mound visits, walk-offs and the final card.</summary>
public class SceneTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(int seed = 1,
        ManagerControl visitor = ManagerControl.Computer, ManagerControl home = ManagerControl.Computer)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), visitor, home);
        return game;
    }

    private static void AssertWellFormed(PlayScript s, string what = "")
    {
        Assert.Equal(PlayScript.CurrentVersion, s.Version);
        Assert.True(s.Duration > 0, what);
        Assert.Equal(s.Actors.Select(a => a.Id).OrderBy(x => x), s.Tracks.Select(t => t.ActorId).OrderBy(x => x));
        Assert.Equal(s.Actors.Count, s.Actors.Select(a => a.Id).Distinct().Count());
        foreach (var t in s.Tracks)
        {
            Assert.Equal(0, t.Keys[0].T);
            for (int i = 1; i < t.Keys.Count; i++)
                Assert.True(t.Keys[i].T >= t.Keys[i - 1].T, $"{what}: {t.ActorId} goes back in time");
            Assert.True(t.Keys[^1].T <= s.Duration + 1e-9, $"{what}: {t.ActorId} is still moving after the end");
            Assert.All(t.Keys, k => Assert.True(k.Z >= 0, what));
        }
        Assert.All(s.Cues, c => Assert.InRange(c.T, 0, s.Duration));
        WallRule.NobodyCrosses(s, what);
        // The script survives the trip to the browser.
        Assert.Equal(s.Actors.Count, PlayScriptJson.Deserialize(PlayScriptJson.Serialize(s))!.Actors.Count);
    }

    private static Key Last(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id).Keys[^1];
    private static Key First(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id).Keys[0];

    [Fact]
    public void AlignmentChange_MovesFieldersBeforeThePitch_WithoutMovingBatterOrRunners()
    {
        var game = NewGame();
        var before = PrePlaySnapshot.Capture(game.State!);
        var scene = SceneChoreographer.AlignmentChange(before, DefensiveAlignment.InfieldIn);
        var target = PlayChoreographer.BuildSetup(before with { Alignment = DefensiveAlignment.InfieldIn });

        AssertWellFormed(scene, "alignment");
        Assert.Equal("AlignmentChange", scene.Outcome);
        Assert.Contains(scene.Cues, c => c.Kind == CueKind.Banner && c.Value == "INFIELD IN");
        Assert.NotEqual(First(scene, "fielder-First"), Last(scene, "fielder-First"));
        foreach (var actor in target.Actors.Where(a => a.Kind != ActorKind.Ball))
        {
            var end = Last(scene, actor.Id);
            var want = First(target, actor.Id);
            Assert.Equal((want.X, want.Y), (end.X, end.Y));
        }
        Assert.Equal(DefensiveAlignment.Standard, game.State!.HomeAlignment);
    }

    // Plays until the half-inning ends. Returns the situation before the last play and the one after it.
    private static (PrePlaySnapshot Before, PrePlaySnapshot After) PlayToNewHalf(GameService game)
    {
        for (int i = 0; i < 400; i++)
        {
            game.ExecutePlay();
            var s = game.State!;
            if (s.Status != GameStatus.InProgress) break;
            var now = PrePlaySnapshot.Capture(s);
            if (now.Inning != game.LastPrePlay!.Inning || now.IsTop != game.LastPrePlay.IsTop)
                return (game.LastPrePlay, now);
        }
        throw new InvalidOperationException("The half-inning never ended.");
    }

    // ── The introduction ─────────────────────────────────────────────────────

    [Fact]
    public void PlayBall_IsWellFormed_AndEndsWithTheFieldAsItIsAtTheFirstPitch()
    {
        var game = NewGame();
        var script = SceneChoreographer.PlayBall(game.State!);
        var setup = PlayChoreographer.BuildSetup(PrePlaySnapshot.Capture(game.State!));

        AssertWellFormed(script, "intro");
        Assert.Equal("PlayBall", script.Outcome);
        Assert.InRange(script.Duration, 5, 30);
        // Everyone who is in the picture before the first pitch is in his place at the end of the introduction.
        foreach (var actor in setup.Actors.Where(a => a.Kind != ActorKind.Ball))
        {
            var want = First(setup, actor.Id);
            var got = Last(script, actor.Id);
            Assert.True(Math.Abs(want.X - got.X) < 0.01 && Math.Abs(want.Y - got.Y) < 0.01, $"{actor.Id} ends in the wrong place");
        }
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Sound && c.Value == "playball");
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.StartsWith("PLAY BALL!"));
    }

    [Fact]
    public void PlayBall_LinesUpBothTeams_OnOppositeSidesOfTheField()
    {
        var game = NewGame();
        var script = SceneChoreographer.PlayBall(game.State!);

        var visitors = script.Actors.Where(a => a.Id.StartsWith("intro-visitor-")).ToList();
        var home = script.Actors.Where(a => a.Id.StartsWith("intro-home-")).ToList();
        Assert.Equal(game.State!.VisitorLineup.Count, visitors.Count);
        Assert.Equal(game.State.HomeLineup.Count, home.Count);

        // At the moment the lineups stand there (just before they leave) visitors are on the first-base side.
        double lined = script.Cues.First(c => c.Kind == CueKind.Banner && c.Value.StartsWith("TODAY")
                                              || c.Kind == CueKind.Banner && c.Value == "THE LINEUPS").T;
        foreach (var a in visitors)
            Assert.True(PositionAt(script, a.Id, lined).X > 0, a.Id);
        foreach (var a in home)
            Assert.True(PositionAt(script, a.Id, lined).X < 0, a.Id);
    }

    [Fact]
    public void WhenTheLineupsBreakUp_TheHomeFieldersGoStraightToTheirPlaces_AndOnlyTheRestGoBackToTheDugout()
    {
        var game = NewGame();
        var script = SceneChoreographer.PlayBall(game.State!);
        var setup = PlayChoreographer.BuildSetup(PrePlaySnapshot.Capture(game.State!));
        double lined = script.Cues.First(c => c.Kind == CueKind.Banner && c.Value.StartsWith("TODAY")
                                              || c.Kind == CueKind.Banner && c.Value == "THE LINEUPS").T;

        int straight = 0;
        foreach (var figure in setup.Actors.Where(a => a.Team == TeamSide.Home && a.Kind is ActorKind.Pitcher or ActorKind.Catcher or ActorKind.Fielder))
        {
            var walker = script.Actors.SingleOrDefault(a => a.Id.StartsWith("intro-home-") && a.Label == figure.Label);
            if (walker is null) continue;   // not in the batting order: he comes from the dugout like anyone else
            straight++;

            var spot = First(setup, figure.Id);
            var walk = script.Tracks.Single(t => t.ActorId == walker.Id).Keys;
            int arrived = walk.ToList().FindIndex(k => k.T > lined && Math.Abs(k.X - spot.X) < 0.01 && Math.Abs(k.Y - spot.Y) < 0.01);
            Assert.True(arrived > 0, $"{walker.Label} walks to his place");
            // From the line to his place he never goes anywhere near the dugout.
            Assert.All(walk.Take(arrived + 1).Where(k => k.T >= lined), k => Assert.False(Math.Abs(k.X) >= 149 && k.Y < 30, $"{walker.Label} goes back to the dugout first"));

            // The same man is the fielder: he is out of the picture until the walker gets there, then he is in his place.
            var field = script.Tracks.Single(t => t.ActorId == figure.Id).Keys;
            Assert.True(Math.Abs(PositionAt(script, figure.Id, lined).X) >= 100, $"{figure.Label} is already on the field");
            Assert.Equal(walk[arrived].T, field[^1].T, 3);
            Assert.True(Math.Abs(field[^1].X - spot.X) < 0.01 && Math.Abs(field[^1].Y - spot.Y) < 0.01);
        }
        Assert.True(straight >= 8, $"{straight} home players go from the line to the field");

        // The first visiting batter does the same to reach the box.
        var batter = script.Actors.Single(a => a.Id == "batter");
        Assert.NotNull(script.Actors.SingleOrDefault(a => a.Id.StartsWith("intro-visitor-") && a.Label == batter.Label));
        Assert.True(Math.Abs(PositionAt(script, "batter", lined).X) >= 100);
    }

    private static FieldPoint PositionAt(PlayScript s, string id, double t)
    {
        var keys = s.Tracks.Single(x => x.ActorId == id).Keys;
        Key a = keys[0];
        foreach (var k in keys) { if (k.T <= t) a = k; else return new(a.X + (k.X - a.X) * (t - a.T) / Math.Max(1e-9, k.T - a.T), a.Y + (k.Y - a.Y) * (t - a.T) / Math.Max(1e-9, k.T - a.T)); }
        return new(a.X, a.Y);
    }

    // ── A new half-inning ────────────────────────────────────────────────────

    [Fact]
    public void ANewHalfInning_SendsTheFieldersOffAndTheNextOnesOn()
    {
        var game = NewGame();
        var (before, after) = PlayToNewHalf(game);

        var scenes = SceneChoreographer.Between(before, after);
        var script = Assert.Single(scenes);

        AssertWellFormed(script, "half-inning");
        Assert.Equal("HalfInning", script.Outcome);

        // The old defense leaves, and is out of the picture (past the edge) by the end.
        foreach (var old in script.Actors.Where(a => a.Id.StartsWith("old-")))
        {
            Assert.True(Math.Abs(Last(script, old.Id).X) > 100, old.Id);
            Assert.Equal(before.IsTop ? TeamSide.Home : TeamSide.Visitor, old.Team);
        }

        // The new defense is in place and so is the batter, at their own spots at the end.
        var setup = PlayChoreographer.BuildSetup(after);
        foreach (var actor in setup.Actors.Where(a => a.Kind != ActorKind.Ball))
        {
            var want = First(setup, actor.Id);
            var got = Last(script, actor.Id);
            Assert.True(Math.Abs(want.X - got.X) < 0.01 && Math.Abs(want.Y - got.Y) < 0.01, $"{actor.Id} ends in the wrong place");
            Assert.Equal(actor.Team, script.Actors.Single(a => a.Id == actor.Id).Team);
        }
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.StartsWith("END OF THE"));
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.Contains("AT BAT"));
    }

    [Fact]
    public void HalfInningScenes_AreWellFormed_ThroughWholeGames()
    {
        int scenes = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            var game = NewGame(seed);
            var last = PrePlaySnapshot.Capture(game.State!);
            while (game.State!.Status == GameStatus.InProgress)
            {
                game.ExecutePlay();
                if (game.State.Status != GameStatus.InProgress) break;
                var now = PrePlaySnapshot.Capture(game.State);
                foreach (var scene in SceneChoreographer.AfterPlay(game.LastPrePlay!, now))
                {
                    AssertWellFormed(scene, $"seed {seed}");
                    scenes++;
                }
                // Nothing else changes between plays, so the scenes before the next pitch are none.
                Assert.Empty(SceneChoreographer.Between(now, now));
                last = now;
            }
        }
        Assert.True(scenes > 50);
    }

    [Fact]
    public void AfterAPlay_TheNextBatterAndNewRunnersAreNotSubstitutions()
    {
        var game = NewGame();
        game.ExecutePlay();
        var now = PrePlaySnapshot.Capture(game.State!);

        // Same half-inning, same pitcher: nothing to show, even though the batter has changed.
        if (now.Inning == game.LastPrePlay!.Inning && now.IsTop == game.LastPrePlay.IsTop
            && now.Pitcher.Id == game.LastPrePlay.Pitcher.Id)
            Assert.Empty(SceneChoreographer.AfterPlay(game.LastPrePlay, now));
    }

    // ── Substitutions ────────────────────────────────────────────────────────

    [Fact]
    public void ANewPitcher_JogsInFromTheBullpen_WhileTheManagerTakesTheOldOneOff()
    {
        var game = NewGame(visitor: ManagerControl.Human);
        var s = game.State!;
        var before = PrePlaySnapshot.Capture(s);
        var reliever = s.HomeBullpen.First();

        game.ChangePitcher(visitor: false, reliever.Player.Id);
        var after = PrePlaySnapshot.Capture(s);

        var script = Assert.Single(SceneChoreographer.Between(before, after));
        AssertWellFormed(script, "pitching change");
        Assert.Equal("PitchingChange", script.Outcome);

        // He starts at the bullpen (past the edge of the picture, behind the wall) and ends on the mound.
        var start = First(script, "pitcher");
        Assert.True(Math.Abs(start.X) > 100 && start.Y < 30);
        Assert.Equal(reliever.Player.Name, script.Actors.Single(a => a.Id == "pitcher").Label);
        var end = Last(script, "pitcher");
        Assert.Equal(FieldGeometry.Mound.X, end.X, 2);
        Assert.Equal(FieldGeometry.Mound.Y, end.Y, 2);

        // The old pitcher and the manager leave.
        Assert.True(Math.Abs(Last(script, "old-pitcher").X) > 100);
        Assert.True(Math.Abs(Last(script, "manager").X) > 100);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.Contains("PITCHING CHANGE"));
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.Contains("IN FOR"));
    }

    [Fact]
    public void APinchHitter_WalksOutFromTheDugout_AndTheBatterHeReplacesWalksOff()
    {
        var game = NewGame(visitor: ManagerControl.Human);
        var s = game.State!;
        var before = PrePlaySnapshot.Capture(s);
        var pinch = s.Bench(true).First();

        game.PinchHit(pinch.Id);
        var after = PrePlaySnapshot.Capture(s);

        var script = Assert.Single(SceneChoreographer.Between(before, after));
        AssertWellFormed(script, "pinch hitter");
        Assert.Equal("Substitutions", script.Outcome);

        Assert.Equal(pinch.Name, script.Actors.Single(a => a.Id == "batter").Label);
        Assert.True(First(script, "batter").X > 100, "the visitors' dugout is on the first-base side");
        Assert.Equal(after.BatsFrom == BatHand.Left ? 3.5 : -3.5, Last(script, "batter").X, 2);
        Assert.True(Math.Abs(Last(script, "old-batter").X) > 100);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.StartsWith("PINCH HITTER"));
    }

    [Fact]
    public void APinchRunner_TakesTheBase_AndTheRunnerHeReplacesLeaves()
    {
        var game = NewGame(visitor: ManagerControl.Human);
        var s = game.State!;
        // Put a runner on first (the play itself does not matter).
        s.RunnerOnFirst = new BaseRunner { Player = s.CurrentBatter, BattingSlot = 0 };
        var before = PrePlaySnapshot.Capture(s);
        var pinch = s.Bench(true).First();

        game.PinchRun(1, pinch.Id);
        var after = PrePlaySnapshot.Capture(s);

        var script = Assert.Single(SceneChoreographer.Between(before, after));
        AssertWellFormed(script, "pinch runner");
        var arrives = Last(script, $"runner-{pinch.Id}");
        Assert.Equal(FieldGeometry.First.X, arrives.X, 2);
        Assert.Equal(FieldGeometry.First.Y, arrives.Y, 2);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.StartsWith("PINCH RUNNER"));
    }

    [Fact]
    public void ASwapOfPositions_IsNotASubstitution()
    {
        var game = NewGame(visitor: ManagerControl.Human, home: ManagerControl.Human);
        var s = game.State!;
        var before = PrePlaySnapshot.Capture(s);
        var lineup = s.HomeLineup;

        game.SwapPositions(visitor: false, lineup[0], lineup[1]);
        var after = PrePlaySnapshot.Capture(s);

        Assert.Empty(SceneChoreographer.Between(before, after));
    }

    // ── The mound visit ──────────────────────────────────────────────────────

    [Fact]
    public void AMoundVisit_GathersTheInfield_ThenSendsEveryoneBack()
    {
        var game = NewGame();
        var at = PrePlaySnapshot.Capture(game.State!);

        var script = SceneChoreographer.MoundVisit(at);

        AssertWellFormed(script, "mound visit");
        var setup = PlayChoreographer.BuildSetup(at);
        foreach (var actor in setup.Actors.Where(a => a.Kind != ActorKind.Ball))
        {
            var want = First(setup, actor.Id);
            var got = Last(script, actor.Id);
            Assert.True(Math.Abs(want.X - got.X) < 0.01 && Math.Abs(want.Y - got.Y) < 0.01, $"{actor.Id} does not go back");
        }
        // Somewhere in the middle the catcher is at the mound.
        var catcher = script.Tracks.Single(t => t.ActorId == "catcher").Keys;
        Assert.Contains(catcher, k => new FieldPoint(k.X, k.Y).DistanceTo(FieldGeometry.Mound) < 12);
    }

    // ── The walk-off ─────────────────────────────────────────────────────────

    private static GameService HomeWinsInTheLastAtBat()
    {
        // Play until the bottom of the ninth, then make the home team score the winning run.
        var game = NewGame(3);
        var s = game.State!;
        s.CurrentInning = 9;
        s.IsTopOfInning = false;
        s.VisitorScoreByInning = [0, 0, 0, 0, 0, 0, 0, 0, 3];
        s.HomeScoreByInning = [0, 0, 0, 0, 0, 0, 0, 0, 0];
        s.HomeScoreByInning[8] = 0;
        game.ExecutePlay();       // a plate appearance, to have a "last play"
        s.HomeScoreByInning[8] = 4;
        s.Status = GameStatus.Final;
        return game;
    }

    [Fact]
    public void AWalkOff_IsRecognised_OnlyWhenTheHomeTeamWinsWhileBatting()
    {
        var game = HomeWinsInTheLastAtBat();
        Assert.True(SceneChoreographer.IsWalkOff(game.State!));

        game.State!.HomeScoreByInning[8] = 0;   // the visitors win instead
        Assert.False(SceneChoreographer.IsWalkOff(game.State));

        game.State.HomeScoreByInning[8] = 4;
        game.State.IsTopOfInning = true;        // the home team won without batting in the ninth
        Assert.False(SceneChoreographer.IsWalkOff(game.State));

        game.State.IsTopOfInning = false;
        game.State.EndedByRain = true;
        Assert.False(SceneChoreographer.IsWalkOff(game.State));
    }

    [Fact]
    public void AWalkOff_HasTheHomeTeamMobTheBatter_WhileTheCrowdStands()
    {
        var game = HomeWinsInTheLastAtBat();
        var script = SceneChoreographer.WalkOff(game.LastPrePlay!, game.State!);

        AssertWellFormed(script, "walk-off");
        Assert.Equal("WalkOff", script.Outcome);

        var mates = script.Actors.Where(a => a.Kind == ActorKind.Teammate).ToList();
        Assert.True(mates.Count >= 9);
        Assert.All(mates, m => Assert.Equal(TeamSide.Home, m.Team));
        foreach (var m in mates)
        {
            var end = Last(script, m.Id);
            Assert.True(new FieldPoint(end.X, end.Y).DistanceFromHome < 15, $"{m.Id} is not at the plate");
            Assert.Equal(Poses.Cheer, end.Pose);
        }
        // They jump (they leave the ground at some point).
        Assert.Contains(script.Tracks.SelectMany(t => t.Keys), k => k.Z > 0 && k.Pose == Poses.Cheer);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "stand");
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Sound && c.Value == "cheer");
    }

    // ── The final card ───────────────────────────────────────────────────────

    [Fact]
    public void TheFinalCard_ShowsTheResult_TheScoreAndTheDecisions()
    {
        var game = HomeWinsInTheLastAtBat();
        var s = game.State!;
        var lines = SceneChoreographer.FinalCard(s);

        Assert.Equal("FINAL", lines[0]);
        Assert.Contains($"{s.VisitorTeam.Abbreviation.ToUpperInvariant()}", lines[1]);
        Assert.EndsWith(s.VisitorTotalScore.ToString(), lines[1]);
        Assert.Contains($"{s.HomeTeam.Abbreviation.ToUpperInvariant()}", lines[2]);
        Assert.EndsWith(s.HomeTotalScore.ToString(), lines[2]);

        s.EndedByRain = true;
        Assert.Equal("FINAL (RAIN)", SceneChoreographer.FinalCard(s)[0]);
    }

    [Fact]
    public void TheFinalScene_IsTheFieldWithTheCardOverIt()
    {
        var game = HomeWinsInTheLastAtBat();
        var scene = SceneChoreographer.FinalScene(game.LastPrePlay!, game.State!);

        Assert.Equal(0, scene.Duration);   // a still picture, like the setup it is made from
        Assert.Equal(scene.Actors.Count, scene.Tracks.Count);
        var card = Assert.Single(scene.Cues, c => c.Kind == CueKind.Card);
        Assert.Equal(0, card.T);
        Assert.StartsWith("FINAL\n", card.Value);
    }

    // ── Home run: the greeting at the plate ──────────────────────────────────

    [Fact]
    public void AHomeRun_IsMetAtThePlateByTeammates_AndTheCrowdStands()
    {
        int found = 0;
        for (int seed = 1; seed <= 12 && found < 4; seed++)
        {
            var game = NewGame(seed);
            while (game.State!.Status == GameStatus.InProgress && found < 4)
            {
                var result = game.ExecutePlay();
                if (result.Outcome != OutcomeType.HomeRun) continue;
                found++;
                var script = PlayChoreographer.Build(game.LastPrePlay!, result, seed);
                AssertWellFormed(script, "home run");

                var mates = script.Actors.Where(a => a.Kind == ActorKind.Teammate).ToList();
                Assert.Equal(3, mates.Count);
                string offense = game.LastPrePlay!.IsTop ? TeamSide.Visitor : TeamSide.Home;
                Assert.All(mates, m => Assert.Equal(offense, m.Team));
                Assert.All(mates, m => Assert.Equal(Poses.Cheer, Last(script, m.Id).Pose));
                Assert.All(mates, m => Assert.True(new FieldPoint(Last(script, m.Id).X, Last(script, m.Id).Y).DistanceFromHome < 20));

                // Every scorer finishes at the plate with his arms up.
                foreach (var move in result.RunnerMoves.Where(m => m.Fate == RunnerFate.Scored))
                {
                    string id = move.FromBase == 0 ? "batter" : $"runner-{move.Player.Id}";
                    Assert.Equal(Poses.Cheer, Last(script, id).Pose);
                }
                Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "stand");
                Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "burst");
            }
        }
        Assert.True(found >= 4, "no home runs came up");
    }

    [Fact]
    public void OtherPlays_HaveNoTeammates()
    {
        var game = NewGame();
        for (int i = 0; i < 80 && game.State!.Status == GameStatus.InProgress; i++)
        {
            var result = game.ExecutePlay();
            if (result.Outcome == OutcomeType.HomeRun) continue;
            var script = PlayChoreographer.Build(game.LastPrePlay!, result, i);
            Assert.DoesNotContain(script.Actors, a => a.Kind == ActorKind.Teammate);
        }
    }
}
