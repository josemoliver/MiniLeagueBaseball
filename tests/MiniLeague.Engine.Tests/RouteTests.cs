using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The wall along each foul line, which nobody walks over: 31 feet outside the line, from the end near home plate out to the pole.</summary>
internal static class WallRule
{
    public static void NobodyCrosses(PlayScript s, string what)
    {
        var ball = s.Actors.Where(a => a.Kind == ActorKind.Ball).Select(a => a.Id).ToHashSet();
        foreach (var t in s.Tracks.Where(t => !ball.Contains(t.ActorId)))
            for (int i = 1; i < t.Keys.Count; i++)
            {
                var a = t.Keys[i - 1];
                var b = t.Keys[i];
                if (b.T - a.T < 1e-6) continue;   // a hand-over: the same man in two figures, nobody moves

                double? last = null;
                for (int k = 0; k <= 80; k++)
                {
                    double x = a.X + (b.X - a.X) * k / 80, y = a.Y + (b.Y - a.Y) * k / 80;
                    if (Math.Abs(x) <= 50) { last = null; continue; }   // the open ground behind home plate
                    double side = Math.Abs(x) - y - 31;                 // positive: on the field side of the wall
                    if (last is double before && Math.Sign(before) * Math.Sign(side) < 0)
                        Assert.Fail($"{what}: {t.ActorId} walks over the wall at ({x:0}, {y:0}) between {a.T:0.##}s and {b.T:0.##}s");
                    last = side;
                }
            }
    }
}

/// <summary>People come and go between the field and the dugouts by the open ground behind home plate.</summary>
public class RouteTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(int seed = 1)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), ManagerControl.Computer, ManagerControl.Computer);
        return game;
    }

    private static IReadOnlyList<Key> Keys(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id).Keys;

    [Fact]
    public void TheRuleCatchesAWalkStraightOverTheWall()
    {
        // From behind the left-field wall straight to shortstop: what the scenes used to do.
        var keys = new[] { new Key(0, -150, 30, 0), new Key(1, -30, 120, 0) };
        var script = new PlayScript(1, "Test", 1, [new Actor("a", ActorKind.Fielder, null, TeamSide.Home)], [new Track("a", keys)], []);

        Assert.ThrowsAny<Exception>(() => WallRule.NobodyCrosses(script, "straight"));
    }

    [Fact]
    public void ANewHalfInning_SendsEveryoneByTheGate_NeverOverTheWall()
    {
        var game = NewGame();
        for (int i = 0; i < 400; i++)
        {
            game.ExecutePlay();
            var now = PrePlaySnapshot.Capture(game.State!);
            if (game.State!.Status != GameStatus.InProgress) break;
            if (now.IsTop != game.LastPrePlay!.IsTop)
            {
                var script = Assert.Single(SceneChoreographer.Between(game.LastPrePlay, now));
                WallRule.NobodyCrosses(script, "half-inning");

                // Someone who comes in from the dugout passes the gate on his way, and the gate is on the open ground.
                var fielder = Keys(script, script.Actors.First(a => a.Kind == ActorKind.Fielder && !a.Id.StartsWith("old-")).Id);
                Assert.Contains(fielder, k => Math.Abs(Math.Abs(k.X) - 30) < 0.01 && Math.Abs(k.Y) < 0.01);
                return;
            }
        }
        Assert.Fail("no half-inning ended");
    }

    [Fact]
    public void TheDugoutsAndTheBullpens_AreOffThePicture_AtGroundLevelBehindTheWall()
    {
        var game = NewGame();
        var before = PrePlaySnapshot.Capture(game.State!);
        game.ChangePitcher(visitor: false, game.State!.HomeBullpen.First().Player.Id);
        var script = Assert.Single(SceneChoreographer.Between(before, PrePlaySnapshot.Capture(game.State)));

        var bullpen = Keys(script, "pitcher")[0];
        Assert.True(Math.Abs(bullpen.X) >= 150 && bullpen.Y < 30, "the reliever starts past the edge, low in the picture");
        var manager = Keys(script, "manager")[0];
        Assert.True(Math.Abs(manager.X) >= 150 && manager.Y < 30);
        WallRule.NobodyCrosses(script, "pitching change");
    }

    [Fact]
    public void NoUmpireIsDrawnInAnyPicture()
    {
        var game = NewGame();
        var at = PrePlaySnapshot.Capture(game.State!);
        var scripts = new[]
        {
            PlayChoreographer.BuildSetup(at),
            PlayChoreographer.Build(at, new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1, Description = "Struck out." }, 1),
            SceneChoreographer.PlayBall(game.State!),
            SceneChoreographer.MoundVisit(at),
            SceneChoreographer.FinalScene(at, game.State!),
        };

        // Only the players, the men who come and go, and the ball.
        foreach (var s in scripts)
            Assert.DoesNotContain(s.Actors, a => a.Id.Contains("umpire", StringComparison.OrdinalIgnoreCase)
                                                 || a.Label?.Contains("umpire", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void WholeGames_NeverSendAnyoneOverTheWall()
    {
        int scenes = 0;
        for (int seed = 1; seed <= 4; seed++)
        {
            var game = NewGame(seed);
            WallRule.NobodyCrosses(SceneChoreographer.PlayBall(game.State!), "play ball");
            while (game.State!.Status == GameStatus.InProgress)
            {
                game.ExecutePlay();
                var now = PrePlaySnapshot.Capture(game.State);
                foreach (var scene in SceneChoreographer.AfterPlay(game.LastPrePlay!, now))
                {
                    WallRule.NobodyCrosses(scene, $"seed {seed}");
                    scenes++;
                }
            }
        }
        Assert.True(scenes > 20);
    }
}
