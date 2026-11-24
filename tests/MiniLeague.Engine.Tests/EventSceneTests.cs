using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>Injuries, ejections and rain delays: the events the game reports, and the scenes that show them.</summary>
public class EventSceneTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

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

    private static void AssertWellFormed(PlayScript s, string what)
    {
        Assert.True(s.Duration > 0, what);
        Assert.Equal(s.Actors.Select(a => a.Id).OrderBy(x => x), s.Tracks.Select(t => t.ActorId).OrderBy(x => x));
        Assert.Equal(s.Actors.Count, s.Actors.Select(a => a.Id).Distinct().Count());
        foreach (var t in s.Tracks)
        {
            for (int i = 1; i < t.Keys.Count; i++)
                Assert.True(t.Keys[i].T >= t.Keys[i - 1].T, $"{what}: {t.ActorId} goes back in time");
            Assert.True(t.Keys[^1].T <= s.Duration + 1e-9, $"{what}: {t.ActorId} moves after the end");
        }
        Assert.All(s.Cues, c => Assert.InRange(c.T, 0, s.Duration));
        WallRule.NobodyCrosses(s, what);
        Assert.Equal(s.Actors.Count, PlayScriptJson.Deserialize(PlayScriptJson.Serialize(s))!.Actors.Count);
    }

    private static Key Last(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id).Keys[^1];

    // ── The game reports what happened ───────────────────────────────────────

    [Fact]
    public void AnInjury_IsReported_WithWhereTheManWas()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.0));
        var s = game.State!;
        var hurt = s.CurrentBatter;

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: hurt);

        var e = Assert.Single(game.TakeEvents());
        Assert.Equal(GameEventKind.Injury, e.Kind);
        Assert.True(e.Visitor);
        Assert.Equal(hurt.Name, e.PlayerName);
        Assert.Equal(InjuryRole.Batter, e.Role);
        Assert.Empty(game.TakeEvents());   // reading them clears them
    }

    [Fact]
    public void AnInjuredRunner_IsReportedOnHisBase()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.25));
        var s = game.State!;
        var runner = s.LineupPlayers(true).Skip(4).First().Player;
        s.RunnerOnFirst = new BaseRunner { Player = runner, ResponsiblePitcherId = s.HomePitcher.Player.Id };

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: s.CurrentBatter);

        var e = Assert.Single(game.TakeEvents());
        Assert.Equal(InjuryRole.Runner, e.Role);
        Assert.Equal(1, e.Base);
    }

    [Fact]
    public void AnInjuredPitcher_IsReported_EvenThoughTheComputerReplacedHimAtOnce()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.3));
        var s = game.State!;
        var hurt = s.HomePitcher.Player;

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: s.CurrentBatter);

        Assert.Null(s.PendingInjury);
        var e = Assert.Single(game.TakeEvents());
        Assert.Equal((InjuryRole.Pitcher, hurt.Name, false), (e.Role, e.PlayerName, e.Visitor));
    }

    [Fact]
    public void AnEjection_IsReported_WithTheTeamOfTheManager()
    {
        var game = NewGame(new GameOptions(false, false, true), new ScriptedRandom(0.0));

        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1 }, wasTop: true);

        var e = Assert.Single(game.TakeEvents());
        Assert.Equal(GameEventKind.Ejection, e.Kind);
        Assert.True(e.Visitor);
    }

    [Fact]
    public void ARainDelay_IsReported_WithItsLength()
    {
        var game = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        game.State!.CurrentInning = 3;

        game.MaybeRainDelay();

        var e = Assert.Single(game.TakeEvents());
        Assert.Equal(GameEventKind.RainDelay, e.Kind);
        Assert.InRange(e.Minutes, 20, 99);
    }

    [Fact]
    public void WithEventsOff_NothingIsReported_AndANewGameStartsClean()
    {
        var game = NewGame(GameOptions.None, new ScriptedRandom(0.0), ManagerControl.Computer);
        for (int i = 0; game.State!.Status == GameStatus.InProgress && i < 400; i++) game.ExecuteComputerTurn();
        Assert.Empty(game.TakeEvents());

        var again = NewGame(new GameOptions(false, false, true), new ScriptedRandom(0.0));
        again.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1 }, wasTop: true);
        again.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"));
        Assert.Empty(again.TakeEvents());
    }

    // ── The ejection scene ───────────────────────────────────────────────────

    [Fact]
    public void AnEjection_HasTheManagerArgueAtThePlate_ThenLeave()
    {
        var game = NewGame(new GameOptions(false, false, true), new ScriptedRandom(0.0));
        var before = PrePlaySnapshot.Capture(game.State!);
        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1 }, wasTop: true);
        var e = game.TakeEvents().Single();

        var script = SceneChoreographer.Ejection(before, e);

        AssertWellFormed(script, "ejection");
        Assert.Equal("Ejection", script.Outcome);
        Assert.DoesNotContain(script.Actors, a => a.Id == "umpire");
        var manager = script.Actors.Single(a => a.Id == "manager");
        Assert.Equal(TeamSide.Visitor, manager.Team);

        // He argues at the plate (arms up), and leaves the picture.
        var keys = script.Tracks.Single(t => t.ActorId == "manager").Keys;
        Assert.Contains(keys, k => k.Pose == Poses.Cheer && Math.Abs(k.X - 5) < 0.1 && Math.Abs(k.Y + 8) < 0.1);
        Assert.True(Math.Abs(Last(script, "manager").X) > 100);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.Contains("EJECTED"));
    }

    // ── The injury scene ─────────────────────────────────────────────────────

    [Fact]
    public void AnInjuredFielder_GoesDown_IsTendedAndHelpedOff_AndHisReplacementComesOn()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.5), ManagerControl.Computer, ManagerControl.Computer);
        var s = game.State!;
        var before = PrePlaySnapshot.Capture(s);
        var hurtEvent = new GameEvent(GameEventKind.Injury, Visitor: false, before.Fielders[Position.Short].Name,
            InjuryRole.Fielder, Position.Short);

        // He is replaced in the lineup, as the game would have done.
        var replacement = s.Bench(false).First();
        s.HomeLineup[s.HomeLineup.IndexOf(before.Fielders[Position.Short].Id)] = replacement.Id;
        s.HomePositions.Remove(before.Fielders[Position.Short].Id);
        s.HomePositions[replacement.Id] = Position.Short;
        var now = PrePlaySnapshot.Capture(s);

        var script = SceneChoreographer.Injury(before, hurtEvent, now);

        AssertWellFormed(script, "injury");
        Assert.Equal("Injury", script.Outcome);
        var injured = script.Tracks.Single(t => t.ActorId == "injured").Keys;
        Assert.Equal(Poses.Down, injured[0].Pose);
        Assert.Contains(injured, k => k.Pose == Poses.Stand);
        Assert.True(Math.Abs(injured[^1].X) > 100, "he is helped off the field");
        Assert.DoesNotContain(script.Actors, a => a.Id == "fielder-Short");   // he stands for no one else at his spot

        // Two trainers run out to him and walk him off.
        Assert.Equal(2, script.Actors.Count(a => a.Id.StartsWith("trainer-")));
        var spot = new FieldPoint(injured[0].X, injured[0].Y);
        Assert.All(Enumerable.Range(0, 2), i =>
            Assert.Contains(script.Tracks.Single(t => t.ActorId == $"trainer-{i}").Keys,
                k => new FieldPoint(k.X, k.Y).DistanceTo(spot) < 6));

        // The replacement takes his place.
        var comes = script.Actors.Single(a => a.Id == "replacement");
        Assert.Equal(replacement.Name, comes.Label);
        Assert.True(new FieldPoint(Last(script, "replacement").X, Last(script, "replacement").Y).DistanceTo(spot) < 0.01);
    }

    [Fact]
    public void AnInjuredPitcher_IsTakenOffTheMound()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.3));
        var s = game.State!;
        var before = PrePlaySnapshot.Capture(s);
        game.MaybeInjurePlayer(Out(), wasTop: true, batter: s.CurrentBatter);
        var e = game.TakeEvents().Single();
        var now = PrePlaySnapshot.Capture(s);

        var script = SceneChoreographer.Injury(before, e, now);

        AssertWellFormed(script, "injured pitcher");
        Assert.DoesNotContain(script.Actors, a => a.Id == "pitcher");   // the mound is his alone
        var start = script.Tracks.Single(t => t.ActorId == "injured").Keys[0];
        Assert.Equal(FieldGeometry.Mound.Y, start.Y, 2);
        Assert.Equal(s.HomePitcher.Player.Name, script.Actors.Single(a => a.Id == "replacement").Label);
    }

    [Fact]
    public void AnInjuryToAHitterOrRunner_ShowsNoReplacement()
    {
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.0));
        var before = PrePlaySnapshot.Capture(game.State!);
        var batter = new GameEvent(GameEventKind.Injury, true, before.Batter.Name, InjuryRole.Batter);
        var runner = new GameEvent(GameEventKind.Injury, true, "Somebody", InjuryRole.Runner, Base: 2);

        foreach (var e in new[] { batter, runner })
        {
            var script = SceneChoreographer.Injury(before, e, before);
            AssertWellFormed(script, e.Role.ToString());
            Assert.DoesNotContain(script.Actors, a => a.Id == "replacement");
            Assert.Equal(Poses.Down, script.Tracks.Single(t => t.ActorId == "injured").Keys[0].Pose);
        }
        var onSecond = SceneChoreographer.Injury(before, runner, before).Tracks.Single(t => t.ActorId == "injured").Keys[0];
        Assert.Equal(FieldGeometry.Second.Y, onSecond.Y, 2);
    }

    [Fact]
    public void AReplacementOnTheOtherTeam_IsNotMistakenForTheHurtMans()
    {
        // The half-inning has changed since: the hurt home fielder's team is batting, so the field shows the other side.
        var game = NewGame(new GameOptions(true, false, false), new ScriptedRandom(0.0, 0.5));
        var before = PrePlaySnapshot.Capture(game.State!);
        var e = new GameEvent(GameEventKind.Injury, Visitor: false, before.Fielders[Position.Left].Name, InjuryRole.Fielder, Position.Left);
        var after = before with { IsTop = false };   // now the visitors are in the field

        var script = SceneChoreographer.Injury(before, e, after);

        Assert.DoesNotContain(script.Actors, a => a.Id == "replacement");
    }

    // ── The rain delay ───────────────────────────────────────────────────────

    [Fact]
    public void ARainDelay_SendsEveryoneInAndBringsThemBackWhenItStops()
    {
        var game = NewGame(new GameOptions(false, true, false), new ScriptedRandom(0.0));
        var situation = PrePlaySnapshot.Capture(game.State!);

        var script = SceneChoreographer.RainDelay(situation, new GameEvent(GameEventKind.RainDelay, true, Minutes: 47));

        AssertWellFormed(script, "rain delay");
        Assert.Equal("RainDelay", script.Outcome);
        var rain = script.Cues.Single(c => c.Kind == CueKind.Effect && c.Value == "rain");
        var clear = script.Cues.Single(c => c.Kind == CueKind.Effect && c.Value == "clear");
        Assert.True(clear.T > rain.T + 3);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.Contains("47 MINUTES"));

        // While it rains everyone is in the dugouts (past the edge of the picture); at the end they are back in place.
        var setup = PlayChoreographer.BuildSetup(situation);
        foreach (var actor in setup.Actors.Where(a => a.Kind != ActorKind.Ball))
        {
            var home = setup.Tracks.Single(t => t.ActorId == actor.Id).Keys[0];
            var end = Last(script, actor.Id);
            Assert.True(Math.Abs(home.X - end.X) < 0.01 && Math.Abs(home.Y - end.Y) < 0.01, $"{actor.Id} does not go back");
            var during = script.Tracks.Single(t => t.ActorId == actor.Id).Keys.Where(k => k.T > rain.T && k.T < clear.T);
            Assert.Contains(during, k => Math.Abs(k.X) > 100);
        }
    }
}
