using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The wind-up and the flight of the ball depend on the pitch that was called for.</summary>
public class PitchStyleTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(int seed = 1)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), ManagerControl.Human, ManagerControl.Human);
        return game;
    }

    private static readonly PitchType[] Pitches = [PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup, PitchType.Pitchout];

    // A strikeout with a given pitch: the simplest play there is, with the pitch as the only thing that differs.
    private static PlayScript StrikeoutWith(PitchType pitch, PrePlaySnapshot situation)
    {
        var result = new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1, Description = "Struck out.", Pitch = pitch };
        return PlayChoreographer.Build(situation, result, 5);
    }

    private static IReadOnlyList<Key> Keys(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id).Keys;

    private static double ArrivalOf(PlayScript s) => Keys(s, "ball").First(k => Math.Abs(k.X) < 0.5 && k.Y < 0).T;

    // ── The game says what was thrown ────────────────────────────────────────

    [Fact]
    public void ThePlayResult_CarriesThePitchTheDefenseCalledFor()
    {
        foreach (var pitch in new[] { PitchType.Curveball, PitchType.Slider, PitchType.Changeup })
        {
            var game = NewGame();
            var result = game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision { PitchType = pitch });
            Assert.Equal(pitch, result.Pitch);
        }
        Assert.Equal(PitchType.Fastball, new PlayResult().Pitch);   // and a fastball when nobody says
    }

    // ── How long it takes ────────────────────────────────────────────────────

    [Fact]
    public void ThePitchesTakeDifferentTimes_TheFastballQuickAndTheChangeupAndCurveSlow()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        var arrival = Pitches.ToDictionary(p => p, p => ArrivalOf(StrikeoutWith(p, situation)));

        Assert.Equal(0.6, arrival[PitchType.Fastball], 3);   // the speed every play was tuned against
        Assert.True(arrival[PitchType.Pitchout] < arrival[PitchType.Fastball]);
        Assert.True(arrival[PitchType.Fastball] < arrival[PitchType.Slider]);
        Assert.True(arrival[PitchType.Slider] < arrival[PitchType.Changeup]);
        Assert.True(arrival[PitchType.Changeup] < arrival[PitchType.Curveball]);
    }

    [Fact]
    public void TheBallIsLetGo_BeforeItArrives_AndTheWholeScriptStillHangsTogether()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        foreach (var pitch in Pitches)
        {
            var s = StrikeoutWith(pitch, situation);
            var ball = Keys(s, "ball");
            Assert.True(ball[1].T < ArrivalOf(s), pitch.ToString());
            foreach (var t in s.Tracks)
                for (int i = 1; i < t.Keys.Count; i++)
                    Assert.True(t.Keys[i].T >= t.Keys[i - 1].T, $"{pitch}: {t.ActorId} goes back in time");
            Assert.True(s.Tracks.SelectMany(t => t.Keys).Max(k => k.T) <= s.Duration + 1e-9, pitch.ToString());
        }
    }

    // ── How he winds up ──────────────────────────────────────────────────────

    [Fact]
    public void EveryPitchButThePitchout_StartsWithTheArmsUp_ThenTheLegKick_ThenTheThrow()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        foreach (var pitch in Pitches.Where(p => p != PitchType.Pitchout))
        {
            var poses = Keys(StrikeoutWith(pitch, situation), "pitcher").Select(k => k.Pose).ToList();
            int windup = poses.IndexOf(Poses.Windup), kick = poses.IndexOf(Poses.Kick);
            int release = poses.FindIndex(p => p is Poses.Throw or Poses.Sidearm);
            Assert.True(windup >= 0 && windup < kick && kick < release, $"{pitch}: {string.Join(",", poses)}");
            Assert.Equal(Poses.Stand, poses[^1]);
        }
    }

    [Fact]
    public void ThePitchoutHasNoWindup()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        var poses = Keys(StrikeoutWith(PitchType.Pitchout, situation), "pitcher").Select(k => k.Pose).ToList();

        Assert.DoesNotContain(Poses.Windup, poses);
        Assert.Contains(Poses.Throw, poses);
    }

    [Fact]
    public void TheCurveballHasTheHighestSlowestKick_AndTheSliderComesThroughSidearm()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        double KickAt(PitchType p) => Keys(StrikeoutWith(p, situation), "pitcher").First(k => k.Pose == Poses.Kick).T;
        double ReleaseAt(PitchType p) => Keys(StrikeoutWith(p, situation), "pitcher").First(k => k.Pose is Poses.Throw or Poses.Sidearm).T;

        Assert.True(KickAt(PitchType.Curveball) > KickAt(PitchType.Fastball));
        Assert.True(ReleaseAt(PitchType.Curveball) > ReleaseAt(PitchType.Fastball));

        Assert.Contains(Keys(StrikeoutWith(PitchType.Slider, situation), "pitcher"), k => k.Pose == Poses.Sidearm);
        Assert.DoesNotContain(Keys(StrikeoutWith(PitchType.Fastball, situation), "pitcher"), k => k.Pose == Poses.Sidearm);
    }

    [Fact]
    public void TheChangeupLooksLikeAFastballUntilTheBallComesOut()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        var fast = Keys(StrikeoutWith(PitchType.Fastball, situation), "pitcher");
        var change = Keys(StrikeoutWith(PitchType.Changeup, situation), "pitcher");

        // The same arm and the same timing, up to the moment the fastball would reach the plate (what he does after the pitch depends on it).
        Assert.Equal(fast.Where(k => k.T < 0.6).Select(k => (k.T, k.Pose)), change.Where(k => k.T < 0.6).Select(k => (k.T, k.Pose)));
    }

    // ── In his hand ──────────────────────────────────────────────────────────

    [Fact]
    public void TheBallIsInThePitchingHand_BeforeItIsThrown_OnTheSideHeThrowsFrom()
    {
        var right = PrePlaySnapshot.Capture(NewGame().State!);
        var left = right with { Pitcher = new Player { Id = right.Pitcher.Id, Name = right.Pitcher.Name, ThrowHand = ThrowHand.Left } };

        foreach (var (situation, side) in new[] { (right, 1), (left, -1) })
            foreach (var pitch in Pitches)
            {
                var ball = Keys(StrikeoutWith(pitch, situation), "ball");
                bool sidearm = pitch == PitchType.Slider;

                Assert.Equal(side * (sidearm ? 6.1 : 2.4), ball[0].X, 2);          // up to his side, not at the middle of him
                Assert.Equal(sidearm ? 7.6 : 12, ball[0].Z, 2);                    // and as high as the hand
                Assert.Equal((ball[0].X, ball[0].Y, ball[0].Z), (ball[1].X, ball[1].Y, ball[1].Z));   // it stays there until it is let go
            }

        // The setup (the field between plays) has it there too.
        var setup = Keys(PlayChoreographer.BuildSetup(right), "ball");
        Assert.Equal(2.4, setup[0].X, 2);
    }

    // ── How the ball travels ─────────────────────────────────────────────────

    [Fact]
    public void ACurveballLoops_AChangeupHangs_AFastballIsStraight()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        double Apex(PitchType p) => Keys(StrikeoutWith(p, situation), "ball").Max(k => k.Apex);

        Assert.Equal(0, Apex(PitchType.Fastball));
        Assert.True(Apex(PitchType.Changeup) > 0);
        Assert.True(Apex(PitchType.Curveball) > Apex(PitchType.Changeup));
    }

    [Fact]
    public void ASliderBreaksLate_ToTheSideTheGloveIsOn_AndLeftAndRightHandersBreakOppositeWays()
    {
        var game = NewGame();
        var right = PrePlaySnapshot.Capture(game.State!);
        var left = right with { Pitcher = new Player { Id = right.Pitcher.Id, Name = right.Pitcher.Name, ThrowHand = ThrowHand.Left } };

        double Bend(PrePlaySnapshot s)
        {
            var ball = Keys(StrikeoutWith(PitchType.Slider, s), "ball");
            Assert.Equal(4, ball.Count);                                    // at rest, let go, break point, plate
            var straight = new FieldPoint(ball[1].X, ball[1].Y).Towards(new FieldPoint(ball[3].X, ball[3].Y), 0.6);
            return ball[2].X - straight.X;
        }

        Assert.True(Bend(right) * Bend(left) < 0, "opposite directions");
        Assert.InRange(Math.Abs(Bend(right)), 0.5, 4);
        Assert.Equal(3, Keys(StrikeoutWith(PitchType.Fastball, right), "ball").Count);   // a fastball has no break point
    }

    [Fact]
    public void OnAPitchout_TheCatcherStepsOutOfTheBox()
    {
        var situation = PrePlaySnapshot.Capture(NewGame().State!);
        var catcher = Keys(StrikeoutWith(PitchType.Pitchout, situation), "catcher");
        var normal = Keys(StrikeoutWith(PitchType.Fastball, situation), "catcher");

        Assert.Contains(catcher, k => Math.Abs(k.X) >= 2.9 && k.T < 0.3);
        Assert.DoesNotContain(normal, k => Math.Abs(k.X) >= 2.9 && k.T < 0.3);
    }

    // ── Left- and right-handers ──────────────────────────────────────────────

    [Fact]
    public void ThePitcherIsMarkedLeftOrRightHanded_InTheSetupAndInEveryScene()
    {
        var game = NewGame();
        var s = game.State!;
        var at = PrePlaySnapshot.Capture(s);
        string expected = at.Pitcher.ThrowHand == ThrowHand.Left ? Hands.Left : Hands.Right;

        Assert.Equal(expected, PlayChoreographer.BuildSetup(at).Actors.Single(a => a.Kind == ActorKind.Pitcher).Hand);
        Assert.Equal(expected, StrikeoutWith(PitchType.Fastball, at).Actors.Single(a => a.Kind == ActorKind.Pitcher).Hand);
        Assert.Equal(expected, SceneChoreographer.MoundVisit(at).Actors.Single(a => a.Kind == ActorKind.Pitcher).Hand);

        // The hand survives the trip to the browser.
        string json = PlayScriptJson.Serialize(PlayChoreographer.BuildSetup(at));
        Assert.Contains($"\"hand\":\"{expected}\"", json);
    }

    // ── Whole games ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryPlayOfWholeGames_StillMakesAWellFormedScript_WhateverTheseMenCallFor()
    {
        var rng = new Random(11);
        int plays = 0;
        for (int seed = 1; seed <= 4; seed++)
        {
            var game = NewGame(seed);
            while (game.State!.Status == GameStatus.InProgress)
            {
                var pitch = Pitches[rng.Next(Pitches.Length)];
                var result = game.ExecutePlay(new OffensiveDecision(), new DefensiveDecision { PitchType = pitch });
                var s = PlayChoreographer.Build(game.LastPrePlay!, result, plays);
                plays++;
                Assert.Equal(pitch, result.Pitch);
                foreach (var t in s.Tracks)
                    for (int i = 1; i < t.Keys.Count; i++)
                        Assert.True(t.Keys[i].T >= t.Keys[i - 1].T, $"{result.Outcome} with {pitch}: {t.ActorId} goes back in time");
                Assert.True(s.Tracks.SelectMany(t => t.Keys).Max(k => k.T) <= s.Duration + 1e-9);
            }
        }
        Assert.True(plays > 200);
    }
}
