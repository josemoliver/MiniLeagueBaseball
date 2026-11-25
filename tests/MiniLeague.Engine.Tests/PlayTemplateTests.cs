using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// The plays beyond the first four: doubles and triples, outs, double plays, errors and steals.
/// The main thing checked is that the picture tells the same story as the result: a runner who is out is
/// beaten by the ball, a runner who is safe beats it, and the ball ends up in someone's hands.
/// </summary>
public class PlayTemplateTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(int seed)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"),
            ManagerControl.Computer, ManagerControl.Computer, options: GameOptions.None);
        return game;
    }

    private static readonly List<(PlayResult Result, PrePlaySnapshot Before)> AllPlays = BuildPlays(60);

    private static List<(PlayResult, PrePlaySnapshot)> BuildPlays(int games)
    {
        var plays = new List<(PlayResult, PrePlaySnapshot)>();
        for (int seed = 1; seed <= games; seed++)
        {
            var game = NewGame(seed);
            int guard = 0;
            while (game.State!.Status == GameStatus.InProgress && guard++ < 2000)
            {
                var result = PlayDriver.Next(game);
                if (result is null) break;
                plays.Add((result, game.LastPrePlay!));
            }
        }
        return plays;
    }

    private static IEnumerable<(PlayResult Result, PrePlaySnapshot Before)> Where(Func<PlayResult, PrePlaySnapshot, bool> match) =>
        AllPlays.Where(p => match(p.Result, p.Before));

    private static (PlayResult Result, PrePlaySnapshot Before) First(Func<PlayResult, PrePlaySnapshot, bool> match) =>
        Where(match).First();

    private static PlayScript Script(PlayResult r, PrePlaySnapshot b, int seed = 1) => PlayChoreographer.Build(b, r, seed);

    private static IReadOnlyList<Key> KeysOf(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id).Keys;

    private static FieldPoint At(Key k) => new(k.X, k.Y);

    private static string RunnerId(Player p) => $"runner-{p.Id}";

    private static string IdOf(RunnerMove m) => m.FromBase == 0 ? "batter" : RunnerId(m.Player);

    // When a runner is last at a base (his arrival).
    private static double Arrival(PlayScript s, string id, FieldPoint spot) =>
        KeysOf(s, id).Last(k => At(k).DistanceTo(spot) < 0.01).T;

    // When the ball is first at a spot after the pitch.
    private static double? BallFirstAt(PlayScript s, FieldPoint spot, double within = 6) =>
        KeysOf(s, "ball").Where(k => k.T > 0.65 && At(k).DistanceTo(spot) < within).Select(k => (double?)k.T).FirstOrDefault();

    private static readonly string[] DefenderIds = ["pitcher", "catcher"];

    private static IEnumerable<Track> Defenders(PlayScript s) =>
        s.Tracks.Where(t => DefenderIds.Contains(t.ActorId) || t.ActorId.StartsWith("fielder-"));

    [Fact]
    public void Hits_RevealTheResultAfterTheDecisiveAction()
    {
        var (homer, beforeHomer) = First((r, _) => r.Outcome == OutcomeType.HomeRun);
        var homeScript = Script(homer, beforeHomer);
        double landing = homeScript.Cues.Single(c => c.Kind == CueKind.Effect && c.Value == "burst").T;
        var homeBanner = homeScript.Cues.Single(c => c.Kind == CueKind.Banner && c.Value == homer.Description);
        Assert.Contains(homeScript.Cues, c => c.Kind == CueKind.Banner && c.T < landing);
        Assert.True(homeBanner.T > landing);
        Assert.Contains(homeScript.Cues, c => c.Kind == CueKind.Hold && c.T == homeBanner.T);

        foreach (var (result, before) in Where((r, _) => r.Outcome is OutcomeType.Single or OutcomeType.Double or OutcomeType.Triple).Take(40))
        {
            var script = Script(result, before);
            var resultBanner = script.Cues.Single(c => c.Kind == CueKind.Banner && c.Value == result.Description);
            double lastRunner = result.RunnerMoves.Where(m => m.Fate != RunnerFate.StayedPut)
                .Select(m => KeysOf(script, IdOf(m)).Last().T).DefaultIfEmpty(0).Max();
            double lastThrow = KeysOf(script, "ball").Last().T;
            Assert.True(resultBanner.T > Math.Max(lastRunner, lastThrow), result.Description);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.T < resultBanner.T);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Hold && c.T == resultBanner.T);
        }
    }

    [Fact]
    public void HomeRun_ClimbsOverTheWall_BeforeTheCrowdAndPitcherReact()
    {
        var (result, before) = First((r, _) => r.Outcome == OutcomeType.HomeRun);
        for (int seed = 0; seed < 20; seed++)
        {
            var script = Script(result, before, seed);
            var ball = KeysOf(script, "ball");
            var apex = ball[^3];
            var wall = ball[^2];
            var seats = ball[^1];
            var clear = script.Cues.Single(c => c.Kind == CueKind.Effect && c.Value == "wallclear");
            Assert.True(apex.T < wall.T && wall.T < seats.T);
            Assert.True(apex.Z > wall.Z && wall.Z > seats.Z);
            Assert.True(Math.Abs(At(wall).DistanceFromHome - FieldGeometry.WallDistance(before.Stadium, At(wall).AngleDegrees)) < 0.1);
            Assert.True(wall.Z > FieldGeometry.WallHeight(before.Stadium, At(wall).AngleDegrees) + 10);
            Assert.Equal(wall.T, clear.T);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "stand" && c.T == clear.T);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == "IT'S GONE!" && c.T == clear.T);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Hold && c.T == clear.T);
            Assert.True(KeysOf(script, "pitcher").First(k => k.Pose == Poses.Slump).T > clear.T);
            Assert.Contains(KeysOf(script, "batter"), k => k.Pose == Poses.Watch && k.T < clear.T);
        }
    }

    [Fact]
    public void CloseCalls_HoldWhenTheRunnerIsCalled()
    {
        foreach (var (result, before) in Where((r, _) => r.Outcome is OutcomeType.GroundOut or OutcomeType.DoublePlay
                     or OutcomeType.StolenBase or OutcomeType.CaughtStealing).Take(50))
        {
            var script = Script(result, before);
            foreach (var move in result.RunnerMoves.Where(m => m.Fate is RunnerFate.ForcedOut or RunnerFate.TaggedOut ||
                         KeysOf(script, IdOf(m)).Any(k => k.Pose == Poses.Slide)))
            {
                double call = KeysOf(script, IdOf(move)).Last().T;
                Assert.Contains(script.Cues, c => c.Kind == CueKind.Hold && c.T == call && c.Value == "0.22");
            }
        }
    }

    [Fact]
    public void DecisiveMoments_HaveEffectsAtTheirFieldPositions()
    {
        foreach (var (result, before) in Where((r, _) => r.Outcome is OutcomeType.Single or OutcomeType.Double
                     or OutcomeType.Triple or OutcomeType.GroundOut or OutcomeType.DoublePlay or OutcomeType.FlyOut).Take(60))
        {
            var script = Script(result, before);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "contact"
                && c.X == FieldGeometry.Home.X && c.Y == FieldGeometry.Home.Y);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "glove" && c.X is not null && c.Y is not null);

            if (result.Outcome == OutcomeType.FlyOut)
                Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "focus" && c.T <
                    script.Cues.First(glove => glove.Kind == CueKind.Effect && glove.Value == "glove").T);
            foreach (var move in result.RunnerMoves.Where(m => m.Fate is RunnerFate.ForcedOut or RunnerFate.TaggedOut))
            {
                double call = KeysOf(script, IdOf(move)).Last().T;
                var bag = FieldGeometry.BaseSpot(move.ToBase);
                Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "call" && c.T == call
                    && Math.Abs(c.X!.Value - bag.X) < 0.01 && Math.Abs(c.Y!.Value - bag.Y) < 0.01);
            }
        }
    }

    // ── Every play ───────────────────────────────────────────────────────────

    [Fact]
    public void EverySimulatedPlay_HasATemplate_SoNoneFallsBackToTheBasicScript()
    {
        // The basic script has only the pitch (the ball ends behind the plate and nobody throws).
        foreach (var (r, b) in AllPlays.Where(p => p.Result.Outcome is not (OutcomeType.Strikeout or OutcomeType.Walk
                     or OutcomeType.HitByPitch or OutcomeType.IntentionalWalk or OutcomeType.StolenBase)))
        {
            var s = Script(r, b);
            Assert.True(KeysOf(s, "ball").Count > 3, $"{r.Outcome}: {r.Description}");
        }
    }

    [Fact]
    public void ABallInPlay_EndsInSomeonesHands()
    {
        foreach (var (r, b) in AllPlays.Where(p => p.Result.Outcome is not (OutcomeType.HomeRun or OutcomeType.HitByPitch)))
        {
            var s = Script(r, b);
            var ball = At(KeysOf(s, "ball")[^1]);
            Assert.True(InTheStands(ball) || Defenders(s).Any(t => At(t.Keys[^1]).DistanceTo(ball) < 8),
                $"{r.Outcome}: ball ends at {ball} where nobody is ({r.Description})");
        }
    }

    [Fact]
    public void WhenTheThrowIsAtABase_AnOutRunnerIsBeatenByIt_AndASafeRunnerBeatsIt()
    {
        int outs = 0, safes = 0;
        foreach (var (r, b) in AllPlays)
        {
            var s = Script(r, b);
            foreach (var m in r.RunnerMoves)
            {
                string id = IdOf(m);
                if (m.IsOut && m.ToBase is >= 1 and <= 3)
                {
                    var spot = FieldGeometry.BaseSpot(m.ToBase);
                    double ball = BallFirstAt(s, spot) ?? throw new Exception($"{r.Outcome}: the ball never reaches {m.ToBase}");
                    double runner = Arrival(s, id, spot);
                    Assert.True(ball < runner, $"{r.Outcome} {id} out at {m.ToBase}: ball {ball:0.00}, runner {runner:0.00}");
                    outs++;
                }
                else if (m.Fate is RunnerFate.Safe or RunnerFate.Scored && m.ToBase > m.FromBase
                         && r.Outcome is not (OutcomeType.Walk or OutcomeType.HitByPitch or OutcomeType.IntentionalWalk or OutcomeType.HomeRun))   // no throw on these
                {
                    int dest = m.Fate == RunnerFate.Scored ? 4 : m.ToBase;
                    var spot = FieldGeometry.BaseSpot(dest);
                    var last = KeysOf(s, "ball")[^1];
                    if (At(last).DistanceTo(spot) > 6) continue;   // the play was not at this base
                    double runner = Arrival(s, id, spot);
                    Assert.True(last.T > runner, $"{r.Outcome} {id} safe at {dest}: ball {last.T:0.00}, runner {runner:0.00}");
                    safes++;
                }
            }
        }
        Assert.True(outs > 300 && safes > 100, $"checked {outs} outs and {safes} safe plays");
    }

    [Fact]
    public void NoOneIsSlowedToAnAbsurdPace_AndNoPlayRunsLong()
    {
        foreach (var (r, b) in AllPlays)
        {
            var s = Script(r, b);
            Assert.True(s.Duration < 11, $"{r.Outcome} runs {s.Duration:0.0}s: {r.Description}");
            foreach (var m in r.RunnerMoves.Where(m => m.IsOut && m.ToBase > 0))
                Assert.True(Arrival(s, IdOf(m), FieldGeometry.BaseSpot(m.ToBase)) < 6, $"{r.Outcome} {IdOf(m)}");
        }
    }

    // ── Ground outs and double plays ─────────────────────────────────────────

    [Fact]
    public void GroundOut_IsFieldedByTheFielderTheResultNames_AndThrownToFirst()
    {
        foreach (var (r, b) in Where((r, b) => r.Outcome == OutcomeType.GroundOut).Take(80))
        {
            var s = Script(r, b);
            var landing = KeysOf(s, "ball").First(k => k.T > 0.65 && k.Ease == Ease.Roll);
            var fielder = KeysOf(s, r.Fielder == Position.P ? "pitcher" : r.Fielder == Position.C ? "catcher" : $"fielder-{r.Fielder}");

            Assert.Contains(fielder, k => k.Pose == Poses.Crouch && At(k).DistanceTo(At(landing)) < 0.01);   // fielded crouching, glove at the grass
            Assert.Contains(s.Cues, c => c.Value == "out");
            var batter = r.RunnerMoves.Single(m => m.FromBase == 0);
            Assert.Equal(RunnerFate.ForcedOut, batter.Fate);
            Assert.Equal(Poses.Out, KeysOf(s, "batter")[^1].Pose);
        }
    }

    [Fact]
    public void AGroundOutFielder_TransfersTheBallBeforeHeThrows()
    {
        var plays = Where((r, _) => r.Outcome == OutcomeType.GroundOut && r.Fielder is Position.Short or Position.Third or Position.Second).Take(40).ToList();
        Assert.NotEmpty(plays);
        foreach (var (r, b) in plays)
        {
            var keys = KeysOf(Script(r, b), $"fielder-{r.Fielder}");
            int turn = keys.ToList().FindIndex(k => k.Pose == Poses.Turn);
            int plant = keys.ToList().FindIndex(k => k.Pose == Poses.Plant);
            int transfer = keys.ToList().FindIndex(k => k.Pose == Poses.Transfer);
            int throwing = keys.ToList().FindIndex(k => k.Pose == Poses.Throw);

            Assert.True(turn >= 0 && turn < plant && plant < transfer && transfer < throwing,
                "the fielder turns, plants and transfers before throwing");
            var script = Script(r, b);
            var first = KeysOf(script, "fielder-First");
            Assert.Contains(first, k => k.Pose == Poses.Reach && k.T < KeysOf(script, "ball")[^1].T);
            Assert.InRange(At(first[^1]).DistanceTo(FieldGeometry.First), 2, 5);
            Assert.True(At(first[^1]).DistanceTo(At(KeysOf(script, "ball")[^1])) < 0.01);
        }
    }

    [Fact]
    public void WithRunnersOn_ThePitcherPitchesFromTheStretch_AndWithTheBasesEmptyHeWindsUp()
    {
        bool Empty(PrePlaySnapshot b) => b.First is null && b.Second is null && b.Third is null;
        var set = Where((r, b) => r.Outcome == OutcomeType.Single && !Empty(b) && r.Pitch != PitchType.Pitchout).Take(20).ToList();
        var empty = Where((r, b) => r.Outcome == OutcomeType.Single && Empty(b) && r.Pitch != PitchType.Pitchout).Take(20).ToList();
        Assert.True(set.Count > 5 && empty.Count > 5);

        foreach (var (r, b) in set)
        {
            var pose = KeysOf(Script(r, b), "pitcher").Select(k => k.Pose).ToList();
            Assert.Contains(Poses.Stretch, pose);
            Assert.DoesNotContain(Poses.Windup, pose);
        }
        foreach (var (r, b) in empty)
        {
            var pose = KeysOf(Script(r, b), "pitcher").Select(k => k.Pose).ToList();
            Assert.Contains(Poses.Windup, pose);
            Assert.DoesNotContain(Poses.Stretch, pose);
        }
    }

    [Fact]
    public void EveryPitchedPlayNamesItsPitchForTheBallInFlight()
    {
        foreach (var (r, b) in Where((r, _) => r.Outcome == OutcomeType.Single).Take(20))
            Assert.Contains(Script(r, b).Cues, c => c.Kind == CueKind.Effect && c.Value == $"pitch:{r.Pitch}");
    }

    [Fact]
    public void Hits_HaveAnotherOutfielderMoveTowardTheBallWithoutTakingTheCatch()
    {
        // NOTE: a bunt single never leaves the infield, so no outfielder has a ball to chase.
        foreach (var (result, before) in Where((r, _) => r.Outcome is OutcomeType.Single or OutcomeType.Double or OutcomeType.Triple
                     && !r.IsBunt).Take(60))
        {
            var script = Script(result, before);
            var glove = script.Cues.First(c => c.Kind == CueKind.Effect && c.Value == "glove");
            var fieldedAt = new FieldPoint(glove.X!.Value, glove.Y!.Value);
            var helpers = FieldGeometry.Outfielders
                .Select(pos => KeysOf(script, $"fielder-{pos}"))
                .Where(keys => keys.Any(k => k.Pose == Poses.Run) &&
                    keys.All(k => k.Pose is not (Poses.Crouch or Poses.Catch)))
                .ToList();

            Assert.NotEmpty(helpers);
            foreach (var keys in helpers)
            {
                Assert.True(At(keys[0]).DistanceTo(At(keys[^1])) >= 5, result.Description);
                Assert.True(At(keys[^1]).DistanceTo(fieldedAt) >= 27, "the supporting fielder leaves room for the pickup");
                Assert.True(FieldGeometry.IsFair(At(keys[^1])));
                Assert.True(keys[1].T >= PlateTime(script));
            }
        }
    }

    [Fact]
    public void PitcherBacksUpThrowsToHomeAndFirst()
    {
        var (hit, beforeHit) = First((r, _) => r.Outcome == OutcomeType.Single && r.RunsScored > 0);
        var homeScript = Script(hit, beforeHit);
        var homePitcher = KeysOf(homeScript, "pitcher");
        Assert.Contains(homePitcher, k => k.Pose == Poses.Run && k.T > PlateTime(homeScript));
        Assert.True(At(homePitcher[^1]).DistanceTo(FieldGeometry.Home) < FieldGeometry.Mound.DistanceTo(FieldGeometry.Home));

        var (groundOut, beforeOut) = First((r, _) => r.Outcome == OutcomeType.GroundOut &&
            r.Fielder is Position.Short or Position.Second or Position.Third);
        var firstScript = Script(groundOut, beforeOut);
        var firstPitcher = KeysOf(firstScript, "pitcher");
        Assert.Contains(firstPitcher, k => k.Pose == Poses.Run && k.T > PlateTime(firstScript));
        Assert.True(At(firstPitcher[^1]).DistanceTo(FieldGeometry.First) < FieldGeometry.Mound.DistanceTo(FieldGeometry.First));
    }

    [Fact]
    public void GroundOut_ByTheFirstBaseman_HasHimStepOnTheBagHimself()
    {
        var (r, b) = First((r, _) => r.Outcome == OutcomeType.GroundOut && r.Fielder == Position.First);
        var s = Script(r, b);

        var ball = KeysOf(s, "ball")[^1];
        Assert.True(At(ball).DistanceTo(FieldGeometry.First) < 0.01);
        Assert.Equal(Poses.Tag, KeysOf(s, "fielder-First")[^1].Pose);
        Assert.True(ball.T < Arrival(s, "batter", FieldGeometry.First));
    }

    [Fact]
    public void DoublePlay_ThrowsToSecondThenToFirst_BeatingBothRunners()
    {
        int checkedPlays = 0;
        foreach (var (r, b) in Where((r, _) => r.Outcome == OutcomeType.DoublePlay).Take(40))
        {
            var s = Script(r, b);
            double atSecond = BallFirstAt(s, FieldGeometry.Second)!.Value;
            var last = KeysOf(s, "ball")[^1];
            Assert.True(At(last).DistanceTo(FieldGeometry.First) < 0.01, "the relay ends at first");
            Assert.True(atSecond < last.T, "second before first");

            var forced = r.RunnerMoves.Single(m => m.FromBase == 1);
            Assert.True(atSecond < Arrival(s, RunnerId(forced.Player), FieldGeometry.Second));
            Assert.True(last.T < Arrival(s, "batter", FieldGeometry.First));
            Assert.Equal(Poses.Out, KeysOf(s, RunnerId(forced.Player))[^1].Pose);
            Assert.Equal(Poses.Out, KeysOf(s, "batter")[^1].Pose);
            Assert.Contains(s.Cues, c => c.Value == "doubleplay");
            // The forced runner slides into second.
            Assert.Contains(KeysOf(s, RunnerId(forced.Player)), k => k.Pose == Poses.Slide);
            checkedPlays++;
        }
        Assert.True(checkedPlays > 5);
    }

    [Fact]
    public void DoublePlay_IsTurnedByTheOtherMiddleInfielder()
    {
        foreach (var (r, b) in Where((r, _) => r.Outcome == OutcomeType.DoublePlay && r.Fielder is Position.Short or Position.Second).Take(40))
        {
            var s = Script(r, b);
            string pivot = r.Fielder == Position.Short ? "fielder-Second" : "fielder-Short";
            var key = KeysOf(s, pivot).Single(k => k.Pose == Poses.Tag);
            Assert.True(At(key).DistanceTo(FieldGeometry.Second) < 0.01);
            var pivotKeys = KeysOf(s, pivot);
            var turn = pivotKeys.Single(k => k.Pose == Poses.Turn);
            var plant = pivotKeys.Single(k => k.Pose == Poses.Plant);
            Assert.True(key.T < turn.T && turn.T < plant.T);
            Assert.True(plant.T < pivotKeys.First(k => k.Pose == Poses.Throw).T);
            Assert.Equal(turn.Facing, plant.Facing);
            Assert.Contains(s.Cues, c => c.Kind == CueKind.Hold && c.T == plant.T && c.Value == "0.18");
        }
    }

    // When the pitch reaches the plate (how long that takes depends on the pitch).
    private static double PlateTime(PlayScript s) => KeysOf(s, "ball").First(k => k.X == 0 && k.Y == 0).T;

    // ── Balls caught in the air ──────────────────────────────────────────────

    [Fact]
    public void FlyOut_IsCaughtByTheNamedFielder_WhoIsThereWhenItComesDown()
    {
        foreach (var (r, b) in Where((r, _) => r.Outcome == OutcomeType.FlyOut).Take(80))
        {
            var s = Script(r, b);
            var catchKey = KeysOf(s, "ball").First(k => k.T > PlateTime(s) + 0.05 && k.Ease == Ease.Arc);
            Assert.True(catchKey.Apex >= 25, "a fly ball goes up");
            string id = r.Fielder == Position.C ? "catcher" : r.Fielder == Position.P ? "pitcher" : $"fielder-{r.Fielder}";
            Assert.Contains(KeysOf(s, id), k => (k.Pose == Poses.Catch || k.Pose == Poses.Dive) && Math.Abs(k.T - catchKey.T) < 0.005
                && At(k).DistanceTo(At(catchKey)) < 0.01);
            Assert.Equal(RunnerFate.Retired, r.RunnerMoves.Single(m => m.FromBase == 0).Fate);
        }
    }

    [Fact]
    public void SacrificeFly_HasTheRunnerLeaveThirdOnlyAfterTheCatch_AndTheThrowArriveLate()
    {
        var plays = Where((r, _) => r.Outcome == OutcomeType.FlyOut && r.RunsScored > 0).Take(15).ToList();
        Assert.NotEmpty(plays);
        foreach (var (r, b) in plays)
            for (int seed = 0; seed < 5; seed++)
            {
                var s = Script(r, b, seed);
                var catchKey = KeysOf(s, "ball").First(k => k.T > PlateTime(s) + 0.05 && k.Ease == Ease.Arc);
                var scorer = r.RunnerMoves.Single(m => m.Fate == RunnerFate.Scored);
                var keys = KeysOf(s, RunnerId(scorer.Player));

                Assert.All(keys.Where(k => k.Pose == Poses.Run), k => Assert.True(k.T >= catchKey.T, "he tagged up"));
                double crossed = Arrival(s, RunnerId(scorer.Player), FieldGeometry.Home);
                var last = KeysOf(s, "ball")[^1];
                Assert.True(last.Y < 0 && last.T > crossed, $"seed {seed}: ball {last.T}, runner {crossed}");
            }
    }

    private static PlayResult Reclassified(PlayResult r, OutcomeType outcome, Position? fielder = null) => new()
    {
        Outcome = outcome, Description = "out", Fielder = fielder, RunnerMoves = r.RunnerMoves,
        NewFirst = r.NewFirst, NewSecond = r.NewSecond, NewThird = r.NewThird, OutsRecorded = 1,
    };

    [Fact]
    public void LineOut_IsAHardLowDriveCaughtByAnInfielder()
    {
        // The simulation does not produce line outs yet, so build one from a fly out's situation.
        var (fly, b) = First((r, _) => r.Outcome == OutcomeType.FlyOut && r.RunsScored == 0);
        for (int seed = 0; seed < 20; seed++)
        {
            var s = Script(Reclassified(fly, OutcomeType.LineOut), b, seed);
            var catchKey = KeysOf(s, "ball").First(k => k.T > PlateTime(s) + 0.05 && k.Ease == Ease.Arc);

            Assert.True(catchKey.Apex < 20, "a line drive stays low");
            Assert.Contains(FieldGeometry.Infielders, p => KeysOf(s, $"fielder-{p}").Any(k => (k.Pose == Poses.Catch || k.Pose == Poses.Dive)
                && Math.Abs(k.T - catchKey.T) < 0.005));
        }
    }

    [Fact]
    public void PopOut_GoesHighAndComesDownNearAnInfielder()
    {
        var (fly, b) = First((r, _) => r.Outcome == OutcomeType.FlyOut && r.RunsScored == 0);
        for (int seed = 0; seed < 20; seed++)
        {
            var s = Script(Reclassified(fly, OutcomeType.PopOut), b, seed);
            var catchKey = KeysOf(s, "ball").First(k => k.T > PlateTime(s) + 0.05 && k.Ease == Ease.Arc);

            Assert.True(catchKey.Apex >= 50, "a pop-up goes high");
            Assert.True(At(catchKey).DistanceFromHome < 150, "and comes down in the infield");
        }
    }

    [Fact]
    public void PlatePop_CatcherTossesHisMask_AndNearbyFieldersYieldToTheCatch()
    {
        var (fly, before) = First((r, _) => r.Outcome == OutcomeType.FlyOut && r.RunsScored == 0);
        foreach (var pos in new[] { Position.C, Position.P, Position.First, Position.Third })
        {
            var script = Script(Reclassified(fly, OutcomeType.PopOut, pos), before, 3);
            var mask = script.Cues.Single(c => c.Kind == CueKind.Effect && c.Value == "mask");
            var ball = KeysOf(script, "ball").First(k => k.T > PlateTime(script) + 0.05 && k.Ease == Ease.Arc);
            string id = pos == Position.C ? "catcher" : pos == Position.P ? "pitcher" : $"fielder-{pos}";
            Assert.Contains(KeysOf(script, id), k => k.Pose == Poses.Catch && Math.Abs(k.T - ball.T) < 0.005
                && At(k).DistanceTo(At(ball)) < 0.01);
            Assert.True(mask.T < ball.T - 1.5, "the mask comes off before the ball drops");
            Assert.Contains(KeysOf(script, "catcher"), k => k.Pose == Poses.MaskOff && k.T < mask.T);
            if (pos != Position.P) Assert.Contains(KeysOf(script, "pitcher"), k => k.Pose == Poses.Track);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == "HIGH POP-UP!");
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Hold && c.T == Math.Round(ball.T, 3) && c.Value == "0.2");
            if (pos == Position.C)
            {
                Assert.True(ball.Y < -6 && Math.Abs(ball.X) >= 8, "the catcher follows a foul pop behind the plate");
                Assert.Contains(KeysOf(script, "catcher"), k => k.Pose == Poses.Catch && k.Facing == "front");
            }
            else if (pos is Position.First or Position.Third)
            {
                Assert.False(FieldGeometry.IsFair(At(ball)));
                Assert.Contains(KeysOf(script, "catcher"), k => k.Pose == Poses.Track && k.T > mask.T);
            }
        }
    }

    [Fact]
    public void UnassignedPopOut_CanBeTakenByTheBatteryOrCornerFielders()
    {
        var (fly, before) = First((r, _) => r.Outcome == OutcomeType.FlyOut && r.RunsScored == 0);
        var caughtBy = new HashSet<string>();
        for (int seed = 0; seed < 100; seed++)
        {
            var script = Script(Reclassified(fly, OutcomeType.PopOut), before, seed);
            var glove = script.Cues.Single(c => c.Kind == CueKind.Effect && c.Value == "glove");
            caughtBy.Add(script.Tracks.First(t => t.ActorId != "ball" && t.Keys.Any(k => k.Pose == Poses.Catch
                && k.T == glove.T && Math.Abs(k.X - glove.X!.Value) < 0.01 && Math.Abs(k.Y - glove.Y!.Value) < 0.01)).ActorId);
        }
        Assert.Contains("catcher", caughtBy);
        Assert.Contains("pitcher", caughtBy);
        Assert.Contains("fielder-First", caughtBy);
        Assert.Contains("fielder-Third", caughtBy);
    }

    // ── Doubles and triples ──────────────────────────────────────────────────

    [Fact]
    public void Double_RollsToTheWall_IsRelayedIn_AndTheBatterSlidesIntoSecond()
    {
        var doubles = Where((r, _) => r.Outcome == OutcomeType.Double).Take(30).ToList();
        Assert.True(doubles.Count > 10);
        foreach (var (r, b) in doubles)
        {
            var s = Script(r, b);
            var ball = KeysOf(s, "ball");
            // The ball runs on to the wall, hits it, and comes back off it to where it is picked up.
            var hit = ball.First(k => k.T > 0.65 && k.Ease == Ease.Roll);
            var rest = ball.First(k => k.T > hit.T);
            double wall = FieldGeometry.WallDistance(b.Stadium, At(rest).AngleDegrees);
            Assert.True(Math.Abs(At(hit).DistanceFromHome - wall) < 0.5, "it reaches the wall");
            Assert.True(At(rest).DistanceFromHome < At(hit).DistanceFromHome, "and comes back off it");

            Assert.True(FieldGeometry.IsFair(At(rest)));
            Assert.True(At(rest).DistanceFromHome > wall - 12 && At(rest).DistanceFromHome < wall, "it ends up at the wall");
            // The relay: the ball is thrown to a cutoff man in the infield before it goes to the base.
            Assert.Contains(ball, k => k.T > rest.T && At(k).DistanceFromHome is > 100 and < 180);
            string cutoff = rest.X < 0 ? "fielder-Short" : "fielder-Second";
            var cutoffKeys = KeysOf(s, cutoff);
            var turn = cutoffKeys.Single(k => k.Pose == Poses.Turn);
            var plant = cutoffKeys.Single(k => k.Pose == Poses.Plant);
            Assert.True(cutoffKeys.First(k => k.Pose == Poses.Catch).T < turn.T && turn.T < plant.T);
            Assert.True(plant.T < cutoffKeys.First(k => k.Pose == Poses.Throw).T);
            Assert.NotNull(plant.Facing);
            Assert.Contains(s.Cues, c => c.Kind == CueKind.Hold && c.T == plant.T && c.Value == "0.18");
            // The batter is on second, and slid in.
            Assert.True(At(KeysOf(s, "batter")[^1]).DistanceTo(FieldGeometry.Second) < 0.01);
            Assert.Contains(KeysOf(s, "batter"), k => k.Pose == Poses.Slide);
        }
    }

    [Fact]
    public void Double_WithARunScoring_HasTheRelayHomeArriveAfterTheRunner()
    {
        var plays = Where((r, _) => r.Outcome == OutcomeType.Double && r.RunsScored > 0).Take(10).ToList();
        Assert.NotEmpty(plays);
        foreach (var (r, b) in plays)
            for (int seed = 0; seed < 5; seed++)
            {
                var s = Script(r, b, seed);
                var last = KeysOf(s, "ball")[^1];
                var scorer = r.RunnerMoves.First(m => m.Fate == RunnerFate.Scored);
                Assert.True(last.Y < 0, "the relay goes home");
                Assert.True(last.T > Arrival(s, RunnerId(scorer.Player), FieldGeometry.Home), $"seed {seed}");
            }
    }

    [Fact]
    public void ScoringHits_ShowASafeSlideAndCatcherSwipeAcrossHome()
    {
        // NOTE: a hit on which another runner is thrown out draws the throw to him instead (NoFreeOptionTests).
        var plays = Where((r, _) => (r.Outcome is OutcomeType.Single or OutcomeType.Double or OutcomeType.Triple) &&
            r.ThrownOutFrom is null &&
            r.RunnerMoves.Any(m => m.Fate == RunnerFate.Scored && m.FromBase <= 2)).Take(30).ToList();
        Assert.NotEmpty(plays);
        foreach (var (result, before) in plays)
        {
            var script = Script(result, before);
            var scorers = result.RunnerMoves.Where(m => m.Fate == RunnerFate.Scored && m.FromBase <= 2).ToList();
            foreach (var scorer in scorers)
            {
                string id = IdOf(scorer);
                var runner = KeysOf(script, id);
                Assert.Contains(runner, k => k.Pose == Poses.Slide);
                Assert.Equal(Poses.Safe, runner[^1].Pose);
                double crossed = Arrival(script, id, FieldGeometry.Home);
                Assert.Contains(script.Cues, c => c.Kind == CueKind.Effect && c.Value == "call" && c.T == crossed
                    && Math.Abs(c.X!.Value) < 0.01 && Math.Abs(c.Y!.Value) < 0.01);
            }
            double lastHome = result.RunnerMoves.Where(m => m.Fate == RunnerFate.Scored)
                .Max(m => Arrival(script, IdOf(m), FieldGeometry.Home));
            var received = KeysOf(script, "ball")[^1];
            var catcher = KeysOf(script, "catcher");
            Assert.True(received.T >= lastHome + 0.175, "the throw reaches home after the safe runner");
            Assert.Contains(catcher, k => k.Pose == Poses.Plant && k.T < received.T);
            Assert.Contains(catcher, k => k.Pose == Poses.Tag && k.T > received.T
                && At(k).DistanceTo(FieldGeometry.Home) < 0.01);
        }
    }

    [Fact]
    public void ClosePlayAtHome_WaitsForTheCatcherTagBeforeTheSafeCall()
    {
        var plays = Where((r, _) => r.Outcome is (OutcomeType.Single or OutcomeType.Double or OutcomeType.Triple)
            && r.RunnerMoves.Any(m => m.Fate == RunnerFate.Scored && m.FromBase <= 2)).Take(60).ToList();
        int closePlays = 0;
        foreach (var (result, before) in plays)
        {
            var script = Script(result, before);
            var call = script.Cues.FirstOrDefault(c => c.Kind == CueKind.Effect && c.Value == "platecall");
            if (call is null) continue;
            closePlays++;

            double crossed = result.RunnerMoves.Where(m => m.Fate == RunnerFate.Scored && m.FromBase <= 2)
                .Max(m => Arrival(script, IdOf(m), FieldGeometry.Home));
            double received = KeysOf(script, "ball")[^1].T;
            double tag = KeysOf(script, "catcher").First(k => k.Pose == Poses.Tag && k.T > received).T;
            double description = script.Cues.First(c => c.Kind == CueKind.Banner && c.Value == result.Description).T;

            Assert.True(crossed < received && received < tag && tag < call.T && call.T < description);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == "HERE COMES THE THROW!"
                && c.T < crossed);
            Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == "SAFE AT HOME!"
                && c.T == call.T);
        }
        Assert.True(closePlays > 0);
    }

    [Fact]
    public void Triple_PutsTheBatterOnThird_AndTheThrowArrivesAfterHim()
    {
        var triples = Where((r, _) => r.Outcome == OutcomeType.Triple).Take(10).ToList();
        Assert.NotEmpty(triples);
        foreach (var (r, b) in triples)
        {
            var s = Script(r, b);
            Assert.True(At(KeysOf(s, "batter")[^1]).DistanceTo(FieldGeometry.Third) < 0.01);
            var last = KeysOf(s, "ball")[^1];
            if (At(last).DistanceTo(FieldGeometry.Third) < 6)
                Assert.True(last.T > Arrival(s, "batter", FieldGeometry.Third));
        }
    }

    [Fact]
    public void ExtraBaseHits_VaryWhereTheyGo()
    {
        var (r, b) = First((r, _) => r.Outcome == OutcomeType.Double);
        var spots = Enumerable.Range(0, 40).Select(seed =>
        {
            var k = KeysOf(Script(r, b, seed), "ball").First(k => k.T > 0.65 && k.Ease == Ease.Roll);
            return (Math.Round(k.X / 25), Math.Round(k.Y / 25));
        }).Distinct().Count();
        Assert.True(spots > 4, $"only {spots} different spots");
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    [Fact]
    public void Error_HasTheFielderMissAndChaseTheBall_AndTheThrowArriveAfterTheRunner()
    {
        var errors = Where((r, _) => r.Outcome == OutcomeType.Error).Take(40).ToList();
        Assert.True(errors.Count > 10);
        foreach (var (r, b) in errors)
        {
            var s = Script(r, b);
            string id = r.ErrorPosition == Position.P ? "pitcher" : r.ErrorPosition == Position.C ? "catcher" : $"fielder-{r.ErrorPosition}";

            Assert.Contains(s.Cues, c => c.Kind == CueKind.Effect && c.Value.StartsWith("error:") && c.X is not null);
            Assert.Contains(KeysOf(s, id), k => k.Pose is Poses.Crouch or Poses.Catch or Poses.Turn);
            Assert.True(KeysOf(s, "ball").Count(k => k.T > 0.65) >= 1);
            Assert.Contains(s.Cues, c => c.Value == "error");
            Assert.Equal(RunnerFate.Safe, r.RunnerMoves.Single(m => m.FromBase == 0).Fate);
            Assert.True(At(KeysOf(s, "batter")[^1]).DistanceTo(FieldGeometry.First) < 0.01);
        }
    }

    private static bool InTheStands(FieldPoint p) => Math.Abs(p.X) > 50 && Math.Abs(p.X) - p.Y > 31;

    // When the pitch reaches the plate or the catcher's glove behind it (a ball in play is at (0, 0), a pitch that is not hit at (0, -4)).
    private static double PitchArrival(PlayScript s) => KeysOf(s, "ball").First(k => Math.Abs(k.X) < 0.5 && k.Y < 0.5).T;

    private static string KindOfError(PlayScript s)
    {
        var kind = s.Cues.Single(c => c.Kind == CueKind.Effect && c.Value.StartsWith("error:")).Value[6..];
        return kind switch { "underglove" => "under", "wildthrow" => "wild", _ => kind };
    }

    [Fact]
    public void Errors_ComeInSeveralKinds_AndEveryOneAgreesWithTheResult()
    {
        var errors = Where((r, _) => r.Outcome == OutcomeType.Error).Take(40).ToList();
        var kinds = new HashSet<string>();
        foreach (var (r, b) in errors)
            for (int seed = 0; seed < 10; seed++)
            {
                var s = Script(r, b, seed);
                Assert.Contains(s.Cues, c => c.Value == "error");
                var batter = r.RunnerMoves.Single(m => m.FromBase == 0);
                Assert.True(At(KeysOf(s, "batter")[^1]).DistanceTo(FieldGeometry.BaseSpot(batter.ToBase)) < 0.01, $"seed {seed}");
                kinds.Add(KindOfError(s));
            }

        Assert.Equal(new[] { "booted", "dropped", "misjudged", "under", "wild" }, kinds.Order());
    }

    [Fact]
    public void ABallUnderTheGlove_GoesRightThroughHim_AndAWildThrowEndsInTheStandsPastFirst()
    {
        bool under = false, wild = false;
        foreach (var (r, b) in Where((r, _) => r.Outcome == OutcomeType.Error && r.ErrorPosition is Position.Short or Position.Second or Position.Third).Take(30))
            for (int seed = 0; seed < 10; seed++)
            {
                var s = Script(r, b, seed);
                string id = $"fielder-{r.ErrorPosition}";
                var kind = KindOfError(s);
                var ball = KeysOf(s, "ball");
                if (kind == "wild")
                {
                    wild = true;
                    var end = At(ball[^1]);
                    Assert.True(end.X * FieldGeometry.First.X > 0 && InTheStands(end), "over the first baseman and into the stands");
                    Assert.Contains(KeysOf(s, "fielder-First"), k => k.Pose == Poses.Catch && k.Z > 0);   // he leaps for it
                }
                if (kind == "under")
                {
                    under = true;
                    var glove = KeysOf(s, id).First(k => k.Pose == Poses.Crouch);
                    var past = ball.First(k => k.T > glove.T && At(k).DistanceFromHome > At(glove).DistanceFromHome + 12);
                    Assert.True(past.T > glove.T, "it carries on past him");
                }
            }
        Assert.True(under && wild);
    }

    [Fact]
    public void Errors_ShowTheMiss_Recovery_AndARealPickupBeforeTheThrow()
    {
        var seen = new HashSet<string>();
        foreach (var (result, before) in Where((r, _) => r.Outcome == OutcomeType.Error).Take(50))
            for (int seed = 0; seed < 12; seed++)
            {
                var script = Script(result, before, seed);
                var cue = script.Cues.Single(c => c.Kind == CueKind.Effect && c.Value.StartsWith("error:"));
                var kind = cue.Value[6..];
                seen.Add(kind);
                Assert.NotNull(cue.X);
                Assert.NotNull(cue.Y);
                if (kind == "wildthrow") continue;

                var pos = result.ErrorPosition!.Value;
                string id = pos == Position.P ? "pitcher" : pos == Position.C ? "catcher" : $"fielder-{pos}";
                var keys = KeysOf(script, id);
                var pickup = keys.First(k => k.T > cue.T && k.Pose == Poses.Crouch);
                Assert.True(pickup.T >= cue.T + 0.49, $"{kind}: the fielder reacts before gathering the ball ({cue.T} -> {pickup.T})");
                // NOTE: after the error: a pitcher's own pitch is also a throw.
                var throwKey = keys.First(k => k.Pose == Poses.Throw && k.T > cue.T);
                Assert.True(throwKey.T > pickup.T);
                if (kind is "booted" or "dropped")
                    Assert.Contains(keys, k => k.Pose == Poses.Recover && k.T > cue.T && k.T < pickup.T);
                if (kind == "underglove")
                    Assert.Contains(keys, k => k.Pose == Poses.Turn && k.T > cue.T && k.T < pickup.T);
                if (kind == "misjudged")
                    Assert.Contains(keys, k => k.Pose == Poses.Turn && k.T < cue.T);
            }
        Assert.Equal(5, seen.Count);
    }

    private static PrePlaySnapshot WithRange(PrePlaySnapshot b, int range) => b with
    {
        Fielders = b.Fielders.ToDictionary(kv => kv.Key, kv => new Player
        {
            Id = kv.Value.Id, Name = kv.Value.Name, HasFieldingData = true,
            Fielding = new Dictionary<Position, FieldingProfile> { [kv.Key] = new FieldingProfile(range, 2, 3) },
        }),
    };

    [Fact]
    public void ABetterFielderDivesForMoreCatches_AndAfterADiveHeGetsUpBeforeHeThrows()
    {
        int Dives(int range)
        {
            int n = 0;
            foreach (var (r, b) in Where((r, _) => r.Outcome == OutcomeType.FlyOut && r.RunsScored == 0).Take(40))
                for (int seed = 0; seed < 10; seed++)
                {
                    var s = Script(r, WithRange(b, range), seed);
                    string id = r.Fielder == Position.C ? "catcher" : r.Fielder == Position.P ? "pitcher" : $"fielder-{r.Fielder}";
                    var dive = KeysOf(s, id).FirstOrDefault(k => k.Pose == Poses.Dive);
                    if (dive is null) continue;
                    n++;
                    var catchKey = KeysOf(s, "ball").First(k => k.T > PlateTime(s) + 0.05 && k.Ease == Ease.Arc);
                    Assert.Equal(1.5, catchKey.Z);                                                     // low, near the grass
                    if (r.Fielder is Position.Left or Position.Center or Position.Right)
                    {
                        Assert.True(dive.T < catchKey.T, "the fielder launches before the ball reaches the glove");
                        Assert.Equal(0, dive.Z);
                        Assert.Contains(KeysOf(s, id), k => k.T > dive.T && k.T < catchKey.T && k.Z == 1.6);
                        Assert.Contains(s.Cues, c => c.Kind == CueKind.Hold && c.T == catchKey.T && c.Value == "0.3");
                    }
                    Assert.Contains(s.Cues, c => c.Kind == CueKind.Banner && c.Value == "DIVING CATCH!");
                    Assert.Contains(KeysOf(s, id), k => k.Pose == Poses.Crouch && k.T > catchKey.T);
                    Assert.Contains(KeysOf(s, id), k => k.Pose == Poses.Stand && k.T > dive.T + 0.5);  // and he gets up
                    Assert.True(InTheStands(At(dive)) == false, "never in the stands");
                    Assert.True(FieldGeometry.IsFair(At(dive)));
                }
            return n;
        }

        int best = Dives(9), worst = Dives(1);

        Assert.True(best > 3 * Math.Max(1, worst), $"{best} dives by the best, {worst} by the worst");
        Assert.True(worst > 0 || best > 20);
    }

    [Fact]
    public void ARunnerTakesALeadAsThePitcherWindsUp_MoreForAFastOne_AndGetsBackOnTheBagIfHeStays()
    {
        // NOTE: not a hit and run: then he is meant to go with the pitch.
        var (r, b) = First((r, b) => r.Outcome == OutcomeType.Strikeout && !r.HitAndRun
            && b.First is not null && b.Second is null && b.Third is null);
        var s = Script(r, b);
        var keys = KeysOf(s, RunnerId(b.First!.Player));

        var lead = keys.First(k => At(k).DistanceTo(FieldGeometry.First) > 3);
        Assert.True(lead.T < PitchArrival(s), "he is off the bag before the pitch gets there");
        Assert.True(lead.Y > FieldGeometry.First.Y && lead.X < FieldGeometry.First.X, "toward second");
        Assert.All(keys.Where(k => k.T < PitchArrival(s)), k => Assert.NotEqual(Poses.Run, k.Pose));   // a shuffle, not a run
        Assert.True(At(keys[^1]).DistanceTo(FieldGeometry.First) < 0.01, "and back on it at the end");

        double Feet(int speed) => Staging.LeadFrom(1, new Player { SpeedRating = speed }).DistanceTo(FieldGeometry.First);
        Assert.True(Feet(1) < Feet(3) && Feet(3) < Feet(5));
        Assert.InRange(Feet(1), 3.9, 4.1);
        Assert.InRange(Feet(5), 9.9, 10.1);
    }

    [Fact]
    public void ARunnerWhoGoesBreaksFromHisLead_SoAStolenBaseStartsFromOffTheBag()
    {
        var (r, b) = First((r, _) => r.Outcome == OutcomeType.StolenBase);
        var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
        var s = Script(r, b);
        var keys = KeysOf(s, RunnerId(runner.Player));

        var run = keys.First(k => k.Pose == Poses.Run && k.T > 0.1);
        Assert.True(At(run).DistanceTo(FieldGeometry.First) > 3, "he breaks from his lead, not from the bag");
        Assert.True(At(keys[^1]).DistanceTo(FieldGeometry.Second) < 0.01);
    }

    [Fact]
    public void ATagAtThePlate_FollowsTheCatchByTheCatcher()
    {
        var (r, b) = First((r, b) => r.Outcome == OutcomeType.Single && r.RunnerMoves.Any(m => m.Fate == RunnerFate.Scored && m.FromBase == 2));
        for (int seed = 0; seed < 5; seed++)
        {
            var keys = KeysOf(Script(r, b, seed), "catcher");
            var tag = keys.FirstOrDefault(k => k.Pose == Poses.Tag);
            if (tag is null) continue;
            Assert.Contains(keys, k => k.Pose == Poses.Catch && k.T < tag.T);
            Assert.Contains(keys, k => k.Pose == Poses.Stand && k.T > tag.T);
            return;
        }
        Assert.Fail("the catcher never tags anybody");
    }

    [Fact]
    public void AStrikeout_HasThePitcherPumpAFist_AndTheBatterHangHisHead()
    {
        var (r, b) = First((r, _) => r.Outcome == OutcomeType.Strikeout);
        var s = Script(r, b);

        var pitcher = KeysOf(s, "pitcher");
        var batter = KeysOf(s, "batter");
        Assert.Contains(pitcher, k => k.Pose == Poses.Cheer && k.T >= PitchArrival(s));
        Assert.Equal(Poses.Stand, pitcher[^1].Pose);
        var slump = batter.First(k => k.Pose == Poses.Slump);
        Assert.True(slump.T > PitchArrival(s));
        Assert.Contains(batter, k => k.Pose == Poses.Run && k.T > slump.T + 0.4);   // he walks off after, not at once
    }

    [Fact]
    public void AHomeRun_LeavesThePitcherWithHisHeadDown()
    {
        var (r, b) = First((r, _) => r.Outcome == OutcomeType.HomeRun);
        var pitcher = KeysOf(Script(r, b), "pitcher");

        Assert.Equal(Poses.Slump, pitcher[^1].Pose);
    }

    // ── Stolen bases ─────────────────────────────────────────────────────────

    // WHY: these pictures are of a lone runner stealing second; steals of third and double steals have their own tests.
    private static bool StealOfSecond(PlayResult r) =>
        r.RunnerMoves.Count(m => m.FromBase > 0) == 1 && r.RunnerMoves.Any(m => m.FromBase == 1);

    [Fact]
    public void StolenBase_HasTheRunnerSlideInSafe_BeforeTheTagArrives()
    {
        var steals = Where((r, _) => r.Outcome == OutcomeType.StolenBase && StealOfSecond(r)).Take(20).ToList();
        Assert.NotEmpty(steals);
        foreach (var (r, b) in steals)
            for (int seed = 0; seed < 5; seed++)
            {
                var s = Script(r, b, seed);
                var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
                var keys = KeysOf(s, RunnerId(runner.Player));
                var ball = KeysOf(s, "ball")[^1];

                Assert.Contains(keys, k => k.Pose == Poses.Slide);
                Assert.Equal(Poses.Safe, keys[^1].Pose);
                Assert.True(At(ball).DistanceTo(FieldGeometry.Second) < 5);
                Assert.True(ball.T > Arrival(s, RunnerId(runner.Player), FieldGeometry.Second), $"seed {seed}");
                Assert.Contains(s.Cues, c => c.Value == "steal");
                var catcher = KeysOf(s, "catcher");
                Assert.True(catcher.First(k => k.Pose == Poses.Pop).T < catcher.First(k => k.Pose == Poses.Plant).T);
                Assert.True(catcher.First(k => k.Pose == Poses.Plant).T < catcher.First(k => k.Pose == Poses.Throw).T);
                var cover = s.Tracks.Single(t => t.Keys.Any(k => k.Pose == Poses.Tag) && t.ActorId.StartsWith("fielder-"));
                Assert.True(cover.Keys.First(k => k.Pose == Poses.Plant).T < ball.T);
                Assert.True(cover.Keys.First(k => k.Pose == Poses.Tag).T > ball.T);
            }
    }

    [Fact]
    public void CaughtStealing_HasTheThrowBeatTheRunner_AndTheFielderTagHim()
    {
        var plays = Where((r, _) => r.Outcome == OutcomeType.CaughtStealing && StealOfSecond(r)).Take(20).ToList();
        Assert.NotEmpty(plays);
        foreach (var (r, b) in plays)
            for (int seed = 0; seed < 5; seed++)
            {
                var s = Script(r, b, seed);
                var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
                var ball = KeysOf(s, "ball")[^1];

                Assert.True(At(ball).DistanceTo(FieldGeometry.Second) < 5);
                Assert.True(ball.T < Arrival(s, RunnerId(runner.Player), FieldGeometry.Second), $"seed {seed}");
                Assert.Equal(Poses.Out, KeysOf(s, RunnerId(runner.Player))[^1].Pose);
                Assert.Contains(new[] { "fielder-Short", "fielder-Second" }, id => KeysOf(s, id)[^1].Pose == Poses.Tag);
                var cover = s.Tracks.Single(t => t.Keys.Any(k => k.Pose == Poses.Tag) && t.ActorId.StartsWith("fielder-"));
                Assert.True(cover.Keys.First(k => k.Pose == Poses.Plant).T < ball.T);
                Assert.True(cover.Keys.First(k => k.Pose == Poses.Tag).T >= Arrival(s, RunnerId(runner.Player), FieldGeometry.Second));
                Assert.Contains(s.Cues, c => c.Value == "caught");
                Assert.DoesNotContain(s.Actors, a => a.Kind == ActorKind.Batter && KeysOf(s, a.Id).Any(k => k.Pose == Poses.Run));
            }
    }

    // ── Outcomes without a template ──────────────────────────────────────────

    [Theory]
    [InlineData(OutcomeType.WildPitch)]
    [InlineData(OutcomeType.PassedBall)]
    [InlineData(OutcomeType.Balk)]
    [InlineData(OutcomeType.TriplePlay)]
    public void OutcomesTheSimNeverProduces_StillGetABasicScript(OutcomeType outcome)
    {
        var (walk, b) = First((r, _) => r.Outcome == OutcomeType.Walk);
        var result = new PlayResult
        {
            Outcome = outcome, Description = outcome.ToString(),
            NewFirst = walk.NewFirst, NewSecond = walk.NewSecond, NewThird = walk.NewThird, RunnerMoves = walk.RunnerMoves,
        };

        var s = Script(result, b);

        Assert.Equal(outcome.ToString(), s.Outcome);
        Assert.Contains(s.Cues, c => c.Kind == CueKind.Banner && c.Value == outcome.ToString());
    }
}
