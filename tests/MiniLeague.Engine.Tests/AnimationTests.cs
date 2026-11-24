using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>Field geometry, the play script, and the choreographer's first templates.</summary>
public class AnimationTests
{
    // ── Field geometry ───────────────────────────────────────────────────────

    [Fact]
    public void Bases_AreNinetyFeetApart_AndMakeARightAngleAtHome()
    {
        Assert.Equal(90, FieldGeometry.Home.DistanceTo(FieldGeometry.First), 3);
        Assert.Equal(90, FieldGeometry.First.DistanceTo(FieldGeometry.Second), 3);
        Assert.Equal(90, FieldGeometry.Second.DistanceTo(FieldGeometry.Third), 3);
        Assert.Equal(90, FieldGeometry.Third.DistanceTo(FieldGeometry.Home), 3);
        // First and third are at right angles as seen from home: their dot product is zero.
        Assert.Equal(0, FieldGeometry.First.X * FieldGeometry.Third.X + FieldGeometry.First.Y * FieldGeometry.Third.Y, 6);
    }

    [Fact]
    public void BaseSpot_FourIsHome()
    {
        Assert.Equal(FieldGeometry.Home, FieldGeometry.BaseSpot(0));
        Assert.Equal(FieldGeometry.Home, FieldGeometry.BaseSpot(4));
        Assert.Equal(FieldGeometry.Second, FieldGeometry.BaseSpot(2));
    }

    [Fact]
    public void WallDistance_PassesThroughThePostedDistances_AndInterpolatesBetweenThem()
    {
        var park = new Stadium { LeftField = 310, LeftCenter = 379, Center = 390, RightCenter = 380, RightField = 302 };
        Assert.Equal(310, FieldGeometry.WallDistance(park, -45), 6);
        Assert.Equal(379, FieldGeometry.WallDistance(park, -22.5), 6);
        Assert.Equal(390, FieldGeometry.WallDistance(park, 0), 6);
        Assert.Equal(380, FieldGeometry.WallDistance(park, 22.5), 6);
        Assert.Equal(302, FieldGeometry.WallDistance(park, 45), 6);
        Assert.Equal((310 + 379) / 2.0, FieldGeometry.WallDistance(park, -33.75), 6);
        Assert.Equal(302, FieldGeometry.WallDistance(park, 80), 6);   // beyond the line is clamped
    }

    [Fact]
    public void WallPoint_IsAtTheWallDistanceInTheRightDirection()
    {
        var park = new Stadium { Center = 400 };
        var p = FieldGeometry.WallPoint(park, 0);
        Assert.Equal(0, p.X, 6);
        Assert.Equal(400, p.Y, 6);

        var right = FieldGeometry.WallPoint(park, 20);
        Assert.True(right.X > 0);
        Assert.Equal(FieldGeometry.WallDistance(park, 20), right.DistanceFromHome, 6);
        Assert.Equal(20, right.AngleDegrees, 6);
    }

    [Fact]
    public void WallHeight_RunsFromTheLeftFieldHeightToTheRightFieldHeight()
    {
        var park = new Stadium { LeftWallHeight = 37, RightWallHeight = 3 };
        Assert.Equal(37, FieldGeometry.WallHeight(park, -45), 6);
        Assert.Equal(3, FieldGeometry.WallHeight(park, 45), 6);
        Assert.Equal(20, FieldGeometry.WallHeight(park, 0), 6);
    }

    private static readonly DefensiveAlignment[] Alignments = Enum.GetValues<DefensiveAlignment>();

    [Fact]
    public void EveryAlignment_PutsEveryFielderInFairTerritory_InsideTheWall()
    {
        var parks = new[]
        {
            Stadium.Generic,
            new Stadium { LeftField = 250, LeftCenter = 260, Center = 270, RightCenter = 260, RightField = 250 },
        };
        foreach (var park in parks)
            foreach (var alignment in Alignments)
                foreach (var hand in new[] { BatHand.Left, BatHand.Right })
                    foreach (var (pos, spot) in FieldGeometry.FielderSpots(alignment, hand, park).Where(f => f.Key != Position.C))
                    {   // (the catcher stands behind the plate)
                        Assert.True(FieldGeometry.IsFair(spot), $"{alignment}/{hand}: {pos} at {spot}");
                        if (FieldGeometry.Outfielders.Contains(pos))
                            Assert.True(spot.DistanceFromHome <=
                                FieldGeometry.WallDistance(park, spot.AngleDegrees) - 14, $"{alignment}/{hand}: {pos}");
                    }
    }

    private static FieldPoint Spot(DefensiveAlignment a, Position p, BatHand hand = BatHand.Right) =>
        FieldGeometry.FielderSpots(a, hand)[p];

    [Fact]
    public void InfieldIn_BringsTheInfieldTowardHome_AndLeavesTheOutfieldAlone()
    {
        foreach (var pos in FieldGeometry.Infielders)
            Assert.True(Spot(DefensiveAlignment.InfieldIn, pos).DistanceFromHome
                < Spot(DefensiveAlignment.Standard, pos).DistanceFromHome - 15);
        foreach (var pos in FieldGeometry.Outfielders)
            Assert.Equal(Spot(DefensiveAlignment.Standard, pos), Spot(DefensiveAlignment.InfieldIn, pos));
    }

    [Fact]
    public void CornersIn_MovesOnlyFirstAndThird()
    {
        foreach (var pos in new[] { Position.First, Position.Third })
            Assert.True(Spot(DefensiveAlignment.CornersIn, pos).DistanceFromHome
                < Spot(DefensiveAlignment.Standard, pos).DistanceFromHome - 15);
        foreach (var pos in new[] { Position.Second, Position.Short, Position.Left, Position.Center, Position.Right })
            Assert.Equal(Spot(DefensiveAlignment.Standard, pos), Spot(DefensiveAlignment.CornersIn, pos));
    }

    [Fact]
    public void GuardLines_PutsFirstAndThirdCloserToTheLines()
    {
        Assert.True(Spot(DefensiveAlignment.GuardLines, Position.First).X
            < Spot(DefensiveAlignment.Standard, Position.First).X);
        Assert.True(Spot(DefensiveAlignment.GuardLines, Position.Third).X
            > Spot(DefensiveAlignment.Standard, Position.Third).X);
        Assert.Equal(Spot(DefensiveAlignment.Standard, Position.Short), Spot(DefensiveAlignment.GuardLines, Position.Short));
    }

    [Fact]
    public void GuardLines_AlsoMovesTheCornerOutfieldersTowardThePoles()
    {
        Assert.True(Spot(DefensiveAlignment.GuardLines, Position.Left).X
            < Spot(DefensiveAlignment.Standard, Position.Left).X);
        Assert.True(Spot(DefensiveAlignment.GuardLines, Position.Right).X
            > Spot(DefensiveAlignment.Standard, Position.Right).X);
        Assert.Equal(Spot(DefensiveAlignment.Standard, Position.Center), Spot(DefensiveAlignment.GuardLines, Position.Center));
    }

    [Fact]
    public void ShiftPull_MovesTheDefenseTowardTheSideTheBatterPulls()
    {
        // A right-handed hitter pulls to left field (negative X); a left-handed hitter to right.
        foreach (var pos in new[] { Position.Short, Position.Second, Position.Left, Position.Center, Position.Right })
        {
            Assert.True(Spot(DefensiveAlignment.ShiftPull, pos, BatHand.Right).X
                < Spot(DefensiveAlignment.Standard, pos).X, $"right-handed {pos}");
            Assert.True(Spot(DefensiveAlignment.ShiftPull, pos, BatHand.Left).X
                > Spot(DefensiveAlignment.Standard, pos).X, $"left-handed {pos}");
        }
    }

    [Fact]
    public void ShiftOpposite_IsTheMirrorOfShiftPull()
    {
        Assert.True(Spot(DefensiveAlignment.ShiftOpposite, Position.Short, BatHand.Right).X
            > Spot(DefensiveAlignment.Standard, Position.Short).X);
        Assert.True(Spot(DefensiveAlignment.ShiftOpposite, Position.Short, BatHand.Left).X
            < Spot(DefensiveAlignment.Standard, Position.Short).X);
    }

    [Fact]
    public void Shift_KeepsTheShiftsDistanceFromHome_SoNobodyMovesCloserOrDeeper()
    {
        foreach (var pos in new[] { Position.Short, Position.Second, Position.Center })
            Assert.Equal(Spot(DefensiveAlignment.Standard, pos).DistanceFromHome,
                Spot(DefensiveAlignment.ShiftPull, pos).DistanceFromHome, 6);
    }

    // ── Script data ──────────────────────────────────────────────────────────

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

    private static IEnumerable<(PlayResult Result, PrePlaySnapshot Before)> Plays(int games)
    {
        for (int seed = 1; seed <= games; seed++)
        {
            var game = NewGame(seed);
            int guard = 0;
            while (game.State!.Status == GameStatus.InProgress && guard++ < 2000)
            {
                var result = game.ExecuteComputerTurn();
                if (result is null) break;
                yield return (result, game.LastPrePlay!);
            }
        }
    }

    private static PlayScript Script(PlayResult r, PrePlaySnapshot b, int seed = 7) =>
        PlayChoreographer.Build(b, r, seed);

    private static (PlayResult Result, PrePlaySnapshot Before) Find(Func<PlayResult, PrePlaySnapshot, bool> match) =>
        Plays(60).First(p => match(p.Result, p.Before));

    private static Track TrackOf(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id);

    private static FieldPoint EndOf(PlayScript s, string id)
    {
        var k = TrackOf(s, id).Keys[^1];
        return new FieldPoint(k.X, k.Y);
    }

    private static string RunnerId(Player p) => $"runner-{p.Id}";

    [Fact]
    public void Script_RoundTripsThroughJson()
    {
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.HomeRun);
        var script = Script(r, b);

        string json = PlayScriptJson.Serialize(script);
        var back = PlayScriptJson.Deserialize(json)!;

        Assert.Equal(json, PlayScriptJson.Serialize(back));
        Assert.Equal(script.Version, back.Version);
        Assert.Equal(script.Actors.Count, back.Actors.Count);
        Assert.Equal(script.Tracks.Sum(t => t.Keys.Count), back.Tracks.Sum(t => t.Keys.Count));
        Assert.Contains("\"kind\":\"ball\"", json);            // enums are camelCase strings
        Assert.DoesNotContain("null", json);                    // nulls are left out
    }

    [Fact]
    public void Script_JsonUsesTheFieldNamesTheBrowserPlayerReads()
    {
        // wwwroot/js/playAnimator.js reads these names and values, so they must not drift.
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.Single);
        var script = Script(r, b);
        using var doc = System.Text.Json.JsonDocument.Parse(PlayScriptJson.Serialize(script));
        var root = doc.RootElement;

        foreach (var name in new[] { "version", "outcome", "duration", "actors", "tracks", "cues" })
            Assert.True(root.TryGetProperty(name, out _), name);

        var actor = root.GetProperty("actors")[0];
        Assert.True(actor.TryGetProperty("id", out _) && actor.TryGetProperty("kind", out _));

        var track = root.GetProperty("tracks")[0];
        Assert.True(track.TryGetProperty("actorId", out _));
        var key = track.GetProperty("keys")[0];
        foreach (var name in new[] { "t", "x", "y", "z", "pose" }) Assert.True(key.TryGetProperty(name, out _), name);
        Assert.Equal(JsonValueKindString(key.GetProperty("pose")), key.GetProperty("pose").GetString());

        var eases = root.GetProperty("tracks").EnumerateArray()
            .SelectMany(t => t.GetProperty("keys").EnumerateArray())
            .Where(k => k.TryGetProperty("ease", out _))
            .Select(k => k.GetProperty("ease").GetString()).ToHashSet();
        Assert.True(eases.IsSubsetOf(new HashSet<string?> { "linear", "arc", "roll" }), string.Join(",", eases));
        Assert.Contains(eases, e => e is "arc" or "roll");
        Assert.All(root.GetProperty("cues").EnumerateArray(),
            c => Assert.Contains(c.GetProperty("kind").GetString(), new[] { "sound", "banner", "effect", "hold" }));
    }

    private static string? JsonValueKindString(System.Text.Json.JsonElement e) => e.GetString();

    [Fact]
    public void Script_IsTheSameForTheSameSituationAndSeed_AndVariesWithTheSeed()
    {
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.Single);

        string a = PlayScriptJson.Serialize(Script(r, b, 11));
        Assert.Equal(a, PlayScriptJson.Serialize(Script(r, b, 11)));

        var others = Enumerable.Range(12, 20).Select(s => PlayScriptJson.Serialize(Script(r, b, s))).ToList();
        Assert.Contains(others, o => o != a);
    }

    // When the pitch reaches the plate (how long that takes depends on the pitch).
    private static double PlateTime(PlayScript s) => TrackOf(s, "ball").Keys.First(k => k.X == 0 && k.Y == 0).T;

    [Fact]
    public void EveryPlay_GetsAWellFormedScript()
    {
        int plays = 0;
        foreach (var (r, b) in Plays(25))
        {
            plays++;
            var s = Script(r, b, plays);
            string what = $"{r.Outcome}: {r.Description}";

            Assert.Equal(PlayScript.CurrentVersion, s.Version);
            Assert.True(s.Duration > 0, what);
            Assert.Equal(s.Actors.Select(a => a.Id).OrderBy(x => x), s.Tracks.Select(t => t.ActorId).OrderBy(x => x));
            Assert.Equal(s.Actors.Count, s.Actors.Select(a => a.Id).Distinct().Count());
            Assert.Single(s.Actors, a => a.Kind == ActorKind.Ball);

            foreach (var t in s.Tracks)
            {
                Assert.Equal(0, t.Keys[0].T);
                for (int i = 1; i < t.Keys.Count; i++)
                    Assert.True(t.Keys[i].T >= t.Keys[i - 1].T, $"{what}: {t.ActorId} goes back in time");
                Assert.True(t.Keys[^1].T <= s.Duration, what);
                Assert.All(t.Keys, k => Assert.True(k.Z >= 0, what));
            }
            Assert.All(s.Cues, c => Assert.InRange(c.T, 0, s.Duration));
            Assert.Contains(s.Cues, c => c.Kind == CueKind.Banner && c.Value == r.Description);
        }
        Assert.True(plays > 800);
    }

    [Fact]
    public void EveryPlay_EndsWithRunnersWhereTheResultPutsThem()
    {
        foreach (var (r, b) in Plays(25))
        {
            var s = Script(r, b);
            foreach (var m in r.RunnerMoves)
            {
                string id = m.FromBase == 0 ? "batter" : RunnerId(m.Player);
                var end = EndOf(s, id);
                string what = $"{r.Outcome} {id} {m.Fate} to {m.ToBase}";
                switch (m.Fate)
                {
                    case RunnerFate.Scored:
                        Assert.True(end.DistanceTo(FieldGeometry.Home) < 0.01, what);
                        break;
                    case RunnerFate.Safe or RunnerFate.ForcedOut or RunnerFate.TaggedOut:
                        Assert.True(end.DistanceTo(FieldGeometry.BaseSpot(m.ToBase)) < 0.01, what);
                        break;
                    case RunnerFate.StayedPut when m.FromBase > 0:
                        Assert.True(end.DistanceTo(FieldGeometry.BaseSpot(m.FromBase)) < 0.01, what);
                        break;
                    case RunnerFate.Retired:
                        Assert.True(end.DistanceFromHome > 20, what);   // walked away from the plate
                        break;
                }
            }
        }
    }

    [Fact]
    public void Fielders_StartWhereTheAlignmentPutsThem()
    {
        var (r, b) = Find((r, b) => r.Outcome == OutcomeType.Strikeout && b.Alignment != DefensiveAlignment.Standard);
        var s = Script(r, b);
        var spots = FieldGeometry.WithOutfieldDepth(
            FieldGeometry.FielderSpots(b.Alignment, b.BatsFrom, b.Stadium),
            FieldGeometry.OutfieldDepthFor(b.Batter.GetBattingStats(b.Pitcher.ThrowHand)), b.Stadium);

        foreach (var pos in b.Fielders.Keys.Where(p => p != Position.C))
        {
            var k = TrackOf(s, $"fielder-{pos}").Keys[0];
            Assert.Equal(spots[pos].X, k.X, 1);
            Assert.Equal(spots[pos].Y, k.Y, 1);
        }
    }

    [Fact]
    public void BuildingScripts_DoesNotChangeTheGame()
    {
        // The choreographer has its own random numbers, so showing plays cannot change how a game goes.
        string Play(bool choreograph)
        {
            var sb = new System.Text.StringBuilder();
            var game = NewGame(5);
            int guard = 0;
            while (game.State!.Status == GameStatus.InProgress && guard++ < 2000)
            {
                var r = game.ExecuteComputerTurn();
                if (r is null) break;
                if (choreograph) Script(r, game.LastPrePlay!);
                sb.Append(r.Description).Append('|').Append(r.PitchesThrown).Append('\n');
            }
            return sb.ToString();
        }

        Assert.Equal(Play(false), Play(true));
    }

    // ── Strikeout ────────────────────────────────────────────────────────────

    [Fact]
    public void Strikeout_PutsTheBallInTheCatchersGloveAndSendsTheBatterAway()
    {
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.Strikeout);
        var s = Script(r, b);

        var ball = EndOf(s, "ball");
        Assert.True(ball.Y < 0 && Math.Abs(ball.X) < 1);                       // behind the plate
        Assert.True(TrackOf(s, "ball").Keys[0].Y > 55);                         // from the pitcher
        Assert.True(EndOf(s, "batter").DistanceFromHome > 20);                  // walks off
        Assert.Contains(s.Cues, c => c.Kind == CueKind.Sound && c.Value == "strikeout");
        Assert.Contains(TrackOf(s, "catcher").Keys, k => k.Pose == Poses.Catch);
    }

    // ── Walks ────────────────────────────────────────────────────────────────

    [Fact]
    public void Walk_SendsTheBatterToFirst_AndTheBallStaysAtTheBackstop()
    {
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.Walk);
        var s = Script(r, b);

        Assert.True(EndOf(s, "batter").DistanceTo(FieldGeometry.First) < 0.01);
        Assert.True(EndOf(s, "ball").Y < 0);
        Assert.Contains(s.Cues, c => c.Kind == CueKind.Sound && c.Value == "walk");
    }

    [Fact]
    public void Walk_WithRunnersOn_ForcesThemAlong()
    {
        var (r, b) = Find((r, b) => r.Outcome == OutcomeType.Walk && b.First is not null && b.Second is not null);
        var s = Script(r, b);

        Assert.True(EndOf(s, RunnerId(b.First!.Player)).DistanceTo(FieldGeometry.Second) < 0.01);
        Assert.True(EndOf(s, RunnerId(b.Second!.Player)).DistanceTo(FieldGeometry.Third) < 0.01);
    }

    [Fact]
    public void HitByPitch_PutsTheBallOnTheBatter()
    {
        // HBP is rare; build the play by hand from an ordinary walk's situation.
        var (walk, b) = Find((r, _) => r.Outcome == OutcomeType.Walk);
        var hbp = new PlayResult
        {
            Outcome = OutcomeType.HitByPitch, Description = "hit by pitch",
            NewFirst = walk.NewFirst, NewSecond = walk.NewSecond, NewThird = walk.NewThird,
            RunnerMoves = walk.RunnerMoves,
        };
        var s = Script(hbp, b);

        var ball = EndOf(s, "ball");
        Assert.True(Math.Abs(ball.X - (b.BatsFrom == BatHand.Left ? 3.5 : -3.5)) < 0.01);
        Assert.Contains(TrackOf(s, "batter").Keys, k => k.Pose == Poses.Hit);
        Assert.True(EndOf(s, "batter").DistanceTo(FieldGeometry.First) < 0.01);
    }

    [Fact]
    public void IntentionalWalk_HasTheCatcherStandAndReceiveTheBallWide()
    {
        var (walk, b) = Find((r, _) => r.Outcome == OutcomeType.Walk);
        var ibb = new PlayResult
        {
            Outcome = OutcomeType.IntentionalWalk, Description = "intentional walk",
            NewFirst = walk.NewFirst, NewSecond = walk.NewSecond, NewThird = walk.NewThird,
            RunnerMoves = walk.RunnerMoves,
        };
        var s = Script(ibb, b);

        Assert.True(Math.Abs(EndOf(s, "ball").X) > 3);
        Assert.True(Math.Abs(EndOf(s, "catcher").X) > 3);
        Assert.True(EndOf(s, "batter").DistanceTo(FieldGeometry.First) < 0.01);
    }

    // ── Home run ─────────────────────────────────────────────────────────────

    [Fact]
    public void HomeRun_LandsBeyondTheWallForThatParkAtThatAngle()
    {
        var parks = new[]
        {
            Stadium.Generic,
            new Stadium { LeftField = 250, LeftCenter = 280, Center = 300, RightCenter = 280, RightField = 250 },
            new Stadium { LeftField = 360, LeftCenter = 430, Center = 450, RightCenter = 430, RightField = 360 },
        };
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.HomeRun);

        foreach (var park in parks)
            for (int seed = 0; seed < 30; seed++)
            {
                var s = Script(r, b with { Stadium = park }, seed);
                var ball = TrackOf(s, "ball").Keys[^1];
                var landing = new FieldPoint(ball.X, ball.Y);

                Assert.True(FieldGeometry.IsFair(landing));
                Assert.True(landing.DistanceFromHome > FieldGeometry.WallDistance(park, landing.AngleDegrees),
                    $"{park.Center}-foot park, seed {seed}");
                Assert.Equal(ball.T, s.Cues.Single(c => c.Value == "burst").T);
            }
    }

    [Fact]
    public void HomeRun_SendsEveryoneAroundToScore_AfterContact()
    {
        var (r, b) = Find((r, b) => r.Outcome == OutcomeType.HomeRun && b.First is not null);
        var s = Script(r, b);

        var contact = s.Cues.Single(c => c.Kind == CueKind.Sound && c.Value == "homerun").T;
        var ids = new List<string> { "batter" };
        ids.AddRange(r.RunnerMoves.Where(m => m.FromBase > 0).Select(m => RunnerId(m.Player)));
        foreach (var id in ids)
        {
            Assert.True(EndOf(s, id).DistanceTo(FieldGeometry.Home) < 0.01, id);
            var track = TrackOf(s, id).Keys;
            // WHY: on a hit and run the first-base runner breaks with the pitch; everybody else waits for contact.
            bool firstRunnerGoing = r.HitAndRun && r.RunnerMoves.Any(m => m.FromBase == 1 && RunnerId(m.Player) == id);
            var early = track.Where(k => k.Pose == Poses.Run && k.T < contact).ToList();
            if (firstRunnerGoing) Assert.NotEmpty(early);
            else Assert.Empty(early);
        }
        // The batter runs out of the box and then to each of the four bases (the last key is him standing at home).
        Assert.Equal(4, TrackOf(s, "batter").Keys.Count(k => k.Pose == Poses.Run));
        var firstRunner = r.RunnerMoves.First(m => m.FromBase == 1);
        Assert.True(TrackOf(s, RunnerId(firstRunner.Player)).Keys.First(k => k.Pose == Poses.Run).T <
            TrackOf(s, "batter").Keys.First(k => k.Pose == Poses.Run).T);
    }

    [Fact]
    public void HomeRun_OutfielderTracksTheBallTowardTheWallBeforeItClears()
    {
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.HomeRun);
        for (int seed = 0; seed < 20; seed++)
        {
            var s = Script(r, b, seed);
            var clear = s.Cues.Single(c => c.Kind == CueKind.Effect && c.Value == "wallclear");
            double angle = new FieldPoint(clear.X!.Value, clear.Y!.Value).AngleDegrees;
            var atWall = FieldPoint.FromPolar(angle, FieldGeometry.WallDistance(b.Stadium, angle) - 4);
            Assert.Contains(FieldGeometry.Outfielders, p =>
            {
                var keys = TrackOf(s, $"fielder-{p}").Keys;
                var track = keys.FirstOrDefault(k => k.Pose == Poses.Track);
                if (track is null) return false;
                var from = new FieldPoint(keys[0].X, keys[0].Y);
                var stop = new FieldPoint(track.X, track.Y);
                return track.T < clear.T && stop.DistanceTo(atWall) <= from.DistanceTo(atWall) + 0.01
                    && keys.Any(k => k.Pose == Poses.Stand && k.T > clear.T && k.Facing == "back");
            });
        }
    }

    [Fact]
    public void FlyOut_SometimesEndsWithALeapingCatchInsideTheParkWall()
    {
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.FlyOut
            && r.Fielder is Position.Left or Position.Center or Position.Right);
        var parks = new[]
        {
            Stadium.Generic,
            new Stadium { LeftField = 250, LeftCenter = 280, Center = 300, RightCenter = 280, RightField = 250 },
            new Stadium { LeftField = 360, LeftCenter = 430, Center = 450, RightCenter = 430, RightField = 360 },
        };

        foreach (var park in parks)
        {
            int wallCatches = 0;
            for (int seed = 0; seed < 80; seed++)
            {
                var s = Script(r, b with { Stadium = park }, seed);
                var leap = TrackOf(s, $"fielder-{r.Fielder}").Keys.FirstOrDefault(k => k.Pose == Poses.Leap);
                if (leap is null) continue;
                wallCatches++;

                var at = new FieldPoint(leap.X, leap.Y);
                double wall = FieldGeometry.WallDistance(park, at.AngleDegrees);
                Assert.True(FieldGeometry.IsFair(at));
                Assert.InRange(wall - at.DistanceFromHome, 4.9, 5.1);
                Assert.Contains(s.Cues, c => c.Kind == CueKind.Banner && c.Value == "CAUGHT AT THE WALL!");
                Assert.Contains(s.Cues, c => c.Kind == CueKind.Effect && c.Value == "wallcatch"
                    && c.X == leap.X && c.Y == leap.Y);
                Assert.Contains(TrackOf(s, "ball").Keys, k => k.X == leap.X && k.Y == leap.Y && k.T > leap.T);
            }
            Assert.InRange(wallCatches, 1, 40);
        }
    }

    // ── Single ───────────────────────────────────────────────────────────────

    [Fact]
    public void Single_PutsTheBallInFairTerritory_AndAFielderGetsToIt()
    {
        foreach (var (r, b) in Plays(25).Where(p => p.Result.Outcome == OutcomeType.Single).Take(60))
        {
            for (int seed = 0; seed < 5; seed++)
            {
                var s = Script(r, b, seed);
                var ball = TrackOf(s, "ball").Keys;
                // The first key after the pitch where the ball is on the ground or coming down is where it lands.
                var landing = ball.First(k => k.T > PlateTime(s) && k.Ease is Ease.Roll or Ease.Arc);
                Assert.True(FieldGeometry.IsFair(new FieldPoint(landing.X, landing.Y)));

                // Someone is standing on the ball when he has it.
                var gatherer = s.Tracks.Where(t => t.ActorId.StartsWith("fielder-"))
                    .Where(t => t.Keys.Any(k => k.Pose is Poses.Catch or Poses.Crouch
                        && Math.Abs(k.X - landing.X) < 0.01 && Math.Abs(k.Y - landing.Y) < 0.01));
                Assert.NotEmpty(gatherer);
            }
        }
    }

    [Fact]
    public void Single_BatterReachesFirst_AndRunnersEndWhereTheResultPutsThem()
    {
        var (r, b) = Find((r, b) => r.Outcome == OutcomeType.Single && b.First is not null);
        var s = Script(r, b);

        Assert.True(EndOf(s, "batter").DistanceTo(FieldGeometry.First) < 0.01);
        var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
        Assert.True(EndOf(s, RunnerId(runner.Player)).DistanceTo(FieldGeometry.BaseSpot(runner.ToBase)) < 0.01);
    }

    [Fact]
    public void Single_WithARunScoring_HasTheThrowHomeArriveAfterTheRunnerCrossesThePlate()
    {
        var (r, b) = Find((r, b) => r.Outcome == OutcomeType.Single && r.RunsScored > 0);
        var scorer = r.RunnerMoves.First(m => m.Fate == RunnerFate.Scored);

        for (int seed = 0; seed < 30; seed++)
        {
            var s = Script(r, b, seed);
            var runner = TrackOf(s, RunnerId(scorer.Player)).Keys;
            double crossed = runner.Last(k => k.DistanceToHome() < 0.01).T;

            // The ball's last key is the throw arriving at the catcher.
            var ball = TrackOf(s, "ball").Keys[^1];
            Assert.True(ball.Y < 0, $"seed {seed}: the throw goes home");
            Assert.True(ball.T > crossed, $"seed {seed}: ball at {ball.T}, runner home at {crossed}");
            Assert.Contains(s.Cues, c => c.Kind == CueKind.Sound && c.Value == "cheer" && Math.Abs(c.T - crossed) < 0.01);
        }
    }

    [Fact]
    public void Single_ThrowsNeverBeatARunnerWhoIsSafe()
    {
        // Whatever target the throw goes to, it arrives after the runner got there.
        foreach (var (r, b) in Plays(25).Where(p => p.Result.Outcome == OutcomeType.Single).Take(80))
            for (int seed = 0; seed < 4; seed++)
            {
                var s = Script(r, b, seed);
                var ball = TrackOf(s, "ball").Keys[^1];
                var at = new FieldPoint(ball.X, ball.Y);

                foreach (var m in r.RunnerMoves.Where(m => m.Fate is RunnerFate.Safe or RunnerFate.Scored && m.ToBase > m.FromBase))
                {
                    int dest = m.Fate == RunnerFate.Scored ? 4 : m.ToBase;
                    if (at.DistanceTo(FieldGeometry.BaseSpot(dest)) > 6) continue;   // the ball went somewhere else

                    string id = m.FromBase == 0 ? "batter" : RunnerId(m.Player);
                    double arrived = TrackOf(s, id).Keys.Last(k =>
                        new FieldPoint(k.X, k.Y).DistanceTo(FieldGeometry.BaseSpot(dest)) < 0.01).T;
                    Assert.True(ball.T > arrived, $"{id} to {dest}, seed {seed}: ball {ball.T}, runner {arrived}");
                }
            }
    }

    [Fact]
    public void Single_VariesWhereTheBallGoes()
    {
        var (r, b) = Find((r, _) => r.Outcome == OutcomeType.Single);
        var landings = Enumerable.Range(0, 40).Select(seed =>
        {
            var script = Script(r, b, seed);
            var k = TrackOf(script, "ball").Keys.First(k => k.T > PlateTime(script) && k.Ease is Ease.Roll or Ease.Arc);
            return (Math.Round(k.X / 20), Math.Round(k.Y / 20));
        }).Distinct().Count();
        Assert.True(landings > 5, $"only {landings} different landing zones");
    }

    // ── Everything else still shows ──────────────────────────────────────────

    [Fact]
    public void OtherOutcomes_StillGetAScript_ShowingThePitchAndTheResult()
    {
        var seen = new HashSet<OutcomeType>();
        foreach (var (r, b) in Plays(25))
        {
            if (r.Outcome is OutcomeType.Strikeout or OutcomeType.Walk or OutcomeType.HomeRun or OutcomeType.Single) continue;
            seen.Add(r.Outcome);
            var s = Script(r, b);
            Assert.Equal(r.Outcome.ToString(), s.Outcome);
            Assert.Contains(s.Cues, c => c.Kind == CueKind.Banner);
        }
        Assert.Contains(OutcomeType.GroundOut, seen);
        Assert.Contains(OutcomeType.CaughtStealing, seen);
    }
}

internal static class KeyExtensions
{
    public static double DistanceToHome(this Key k) => new FieldPoint(k.X, k.Y).DistanceFromHome;
}

/// <summary>The between-plays picture of the field.</summary>
public class SetupScriptTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static GameService NewGame()
    {
        var game = new GameService(new AtBatResolver(new Random(2)), new ComputerManager(new Random(2)), new Random(2));
        game.StartGame(
            TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", "1927-nyy.json"))),
            TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", "1975-cin.json"))),
            ManagerControl.Computer, ManagerControl.Computer, options: GameOptions.None);
        return game;
    }

    [Fact]
    public void Setup_IsAZeroLengthScript_WithTheFieldersBatterAndRunnersInPlace()
    {
        var game = NewGame();
        var s = game.State!;
        s.RunnerOnFirst = new BaseRunner { Player = s.OffenseTeam.Roster[3] };
        s.RunnerOnThird = new BaseRunner { Player = s.OffenseTeam.Roster[4] };

        var before = PrePlaySnapshot.Capture(s);
        var script = PlayChoreographer.BuildSetup(before);

        Assert.Equal(0, script.Duration);
        Assert.All(script.Tracks, t => Assert.Single(t.Keys));
        Assert.Empty(script.Cues);
        Assert.Equal(1, script.Actors.Count(a => a.Kind == ActorKind.Batter));
        Assert.Equal(2, script.Actors.Count(a => a.Kind == ActorKind.Runner));
        Assert.Equal(1, script.Actors.Count(a => a.Kind == ActorKind.Pitcher));
        Assert.Equal(1, script.Actors.Count(a => a.Kind == ActorKind.Catcher));
        Assert.Equal(7, script.Actors.Count(a => a.Kind == ActorKind.Fielder));
        Assert.Equal(Hands.Of(before.Pitcher.ThrowHand), script.Actors.Single(a => a.Id == "pitcher").Hand);
        foreach (var (position, player) in before.Fielders)
        {
            var id = position == Position.C ? "catcher" : $"fielder-{position}";
            Assert.Equal(Hands.Of(player.ThrowHand), script.Actors.Single(a => a.Id == id).Hand);
        }

        var first = script.Tracks.Single(t => t.ActorId == $"runner-{s.RunnerOnFirst.Player.Id}").Keys[0];
        Assert.Equal(FieldGeometry.First.X, first.X, 2);
        Assert.Equal(FieldGeometry.First.Y, first.Y, 2);
    }

    [Fact]
    public void Setup_ShowsTheBattingTeamAsTheOneAtBat()
    {
        var game = NewGame();
        var script = PlayChoreographer.BuildSetup(PrePlaySnapshot.Capture(game.State!));

        Assert.Equal(TeamSide.Visitor, script.Actors.Single(a => a.Kind == ActorKind.Batter).Team);   // top of the first
        Assert.Equal(TeamSide.Home, script.Actors.Single(a => a.Kind == ActorKind.Pitcher).Team);
    }
}
