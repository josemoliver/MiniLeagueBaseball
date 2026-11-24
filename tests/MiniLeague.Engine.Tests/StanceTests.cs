using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>How a batter stands: which side, and the stance his kind of hitting calls for.</summary>
public class StanceTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(ManagerControl visitor = ManagerControl.Human)
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)), new Random(1));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), visitor, ManagerControl.Computer);
        return game;
    }

    private static Player Hitter(int atBats, int hits, int doubles, int homeRuns, BatHand hand = BatHand.Right) => new()
    {
        Id = 900, Name = "Test Hitter", BatHand = hand,
        Overall = new BatterStats { AtBats = atBats, Hits = hits, Doubles = doubles, HomeRuns = homeRuns },
    };

    // ── Which stance ─────────────────────────────────────────────────────────

    [Fact]
    public void AHitterWithPower_StandsWithHisFeetApart()
    {
        // .280 with 40 doubles and 45 homers in 550 at-bats: slugging far above his average.
        Assert.Equal(Stances.Power, Stances.Of(Hitter(550, 154, 40, 45), ThrowHand.Right));
    }

    [Fact]
    public void ASlapHitter_Crouches()
    {
        // .310 with almost no extra-base hits.
        Assert.Equal(Stances.Slap, Stances.Of(Hitter(550, 170, 5, 1), ThrowHand.Right));
    }

    [Fact]
    public void AnOrdinaryHitter_AndOneWithNoRecord_StandAsAnyoneDoes()
    {
        Assert.Equal(Stances.Balanced, Stances.Of(Hitter(550, 150, 30, 18), ThrowHand.Right));   // .273 with an isolated power of .152
        Assert.Equal(Stances.Balanced, Stances.Of(new Player { BatHand = BatHand.Right }, ThrowHand.Right));
        Assert.Equal(Stances.Balanced, Stances.Of(Hitter(20, 12, 0, 6), ThrowHand.Right));   // too few at-bats to judge
    }

    [Fact]
    public void EveryShippedTeamHasHittersOfAllThreeKinds()
    {
        var kinds = new HashSet<string>();
        foreach (var path in TeamLoader.ParseIndex(File.ReadAllText(Path.Combine(DataDir, "teams.json"))))
            foreach (var p in TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, path))).Batters)
                kinds.Add(Stances.Of(p, ThrowHand.Right));

        Assert.Equal(new[] { Stances.Balanced, Stances.Power, Stances.Slap }, kinds.OrderBy(k => k));
    }

    // ── It reaches the picture ───────────────────────────────────────────────

    [Fact]
    public void TheBatter_IsMarkedWithHisSideAndHisStance_InEveryPlayAndScene()
    {
        var game = NewGame();
        var at = PrePlaySnapshot.Capture(game.State!);
        string side = at.BatsFrom == BatHand.Left ? Hands.Left : Hands.Right;
        string stance = Stances.Of(at.Batter, at.Pitcher.ThrowHand);

        var setup = PlayChoreographer.BuildSetup(at).Actors.Single(a => a.Kind == ActorKind.Batter);
        Assert.Equal((side, stance), (setup.Hand, setup.Stance));

        var result = new PlayResult { Outcome = OutcomeType.Strikeout, OutsRecorded = 1, Description = "Struck out." };
        var play = PlayChoreographer.Build(at, result, 1).Actors.Single(a => a.Kind == ActorKind.Batter);
        Assert.Equal((side, stance), (play.Hand, play.Stance));

        var visit = SceneChoreographer.MoundVisit(at).Actors.Single(a => a.Kind == ActorKind.Batter);
        Assert.Equal((side, stance), (visit.Hand, visit.Stance));

        string json = PlayScriptJson.Serialize(PlayChoreographer.BuildSetup(at));
        Assert.Contains($"\"stance\":\"{stance}\"", json);
        Assert.Contains($"\"hand\":\"{side}\"", json);
    }

    [Fact]
    public void ALeftHandedHitter_StandsOnTheFirstBaseSide_AndARightHanderOnTheThird()
    {
        var game = NewGame();
        var at = PrePlaySnapshot.Capture(game.State!);

        foreach (var hand in new[] { BatHand.Left, BatHand.Right })
        {
            var script = PlayChoreographer.BuildSetup(at with { BatsFrom = hand });
            var batter = script.Tracks.Single(t => t.ActorId == "batter").Keys[0];
            Assert.Equal(hand == BatHand.Left ? 3.5 : -3.5, batter.X);
            Assert.Equal(hand == BatHand.Left ? Hands.Left : Hands.Right, script.Actors.Single(a => a.Kind == ActorKind.Batter).Hand);
        }
    }

    // ── Switch hitters ───────────────────────────────────────────────────────

    private static (PrePlaySnapshot Before, PrePlaySnapshot After) SwitchHitterFacingANewPitcher()
    {
        var game = NewGame();
        var s = game.State!;
        // Make the man at the plate a switch hitter, against a right-hander first and then a left-hander.
        var batter = s.CurrentBatter;
        batter.BatHand = BatHand.Switch;
        var before = PrePlaySnapshot.Capture(s);

        var lefty = s.HomeBullpen.Select(w => w.Player).FirstOrDefault(p => p.ThrowHand == ThrowHand.Left)
                    ?? s.HomeBullpen.First().Player;
        lefty.ThrowHand = ThrowHand.Left;
        game.ChangePitcher(visitor: false, lefty.Id);
        return (before, PrePlaySnapshot.Capture(s));
    }

    [Fact]
    public void ASwitchHitter_BatsFromTheOppositeSideOfThePitchersArm()
    {
        var (before, after) = SwitchHitterFacingANewPitcher();

        Assert.Equal(BatHand.Left, before.BatsFrom);    // against the right-hander
        Assert.Equal(BatHand.Right, after.BatsFrom);    // against the left-hander
    }

    [Fact]
    public void WhenAPitchingChangeTurnsHimAround_TheSwitchHitterWalksRoundBehindTheCatcher()
    {
        var (before, after) = SwitchHitterFacingANewPitcher();

        var script = Assert.Single(SceneChoreographer.Between(before, after));

        var keys = script.Tracks.Single(t => t.ActorId == "batter").Keys;
        Assert.Equal(3.5, keys[0].X);                        // starts in the left-hander's box (first-base side)
        Assert.Equal(-3.5, keys[^1].X);                      // ends in the right-hander's
        Assert.Contains(keys, k => Math.Abs(k.X) < 1 && k.Y < -5);   // by way of the back of the plate
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value.Contains("SWITCHES TO BAT RIGHT"));
        Assert.Equal(Hands.Right, script.Actors.Single(a => a.Kind == ActorKind.Batter).Hand);
        Assert.True(keys.Zip(keys.Skip(1)).All(p => p.Second.T >= p.First.T));
    }

    [Fact]
    public void ANewPitcherOfTheSameArm_LeavesTheSwitchHitterWhereHeIs()
    {
        var game = NewGame();
        var s = game.State!;
        s.CurrentBatter.BatHand = BatHand.Switch;
        var before = PrePlaySnapshot.Capture(s);
        var reliever = s.HomeBullpen.Select(w => w.Player).First();
        reliever.ThrowHand = before.Pitcher.ThrowHand;
        game.ChangePitcher(visitor: false, reliever.Id);

        var script = Assert.Single(SceneChoreographer.Between(before, PrePlaySnapshot.Capture(s)));

        var keys = script.Tracks.Single(t => t.ActorId == "batter").Keys;
        Assert.All(keys, k => Assert.Equal(keys[0].X, k.X));
        Assert.DoesNotContain(script.Cues, c => c.Value.Contains("SWITCHES"));
    }
}
