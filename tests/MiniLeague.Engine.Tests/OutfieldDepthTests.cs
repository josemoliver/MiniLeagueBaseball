using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The outfield plays deeper for a power hitter and shallower for a slap hitter (for show only).</summary>
public class OutfieldDepthTests
{
    private static BatterStats Stats(int atBats, int hits, int doubles, int triples, int homers) => new()
    {
        AtBats = atBats, Hits = hits, Doubles = doubles, Triples = triples, HomeRuns = homers,
    };

    // A typical hitter has isolated power (slugging minus average) of about .150.
    private static readonly BatterStats Typical = Stats(500, 140, 25, 3, 12);    // .280 / .430 slugging
    private static readonly BatterStats Slugger = Stats(500, 150, 30, 2, 50);    // .300 average, .690 slugging
    private static readonly BatterStats Slapper = Stats(500, 150, 8, 4, 1);      // .300 average, .350 slugging

    [Fact]
    public void ADepthForEveryKindOfHitter_IsBetweenTenFeetInAndTwelveOut()
    {
        double typical = FieldGeometry.OutfieldDepthFor(Typical);
        double slugger = FieldGeometry.OutfieldDepthFor(Slugger);
        double slapper = FieldGeometry.OutfieldDepthFor(Slapper);

        Assert.InRange(typical, -3, 3);
        Assert.Equal(12, slugger, 6);       // the most
        Assert.True(slapper < -3 && slapper >= -10, $"slap hitter: {slapper}");
        Assert.True(slugger > typical && typical > slapper);
    }

    [Fact]
    public void WithoutEnoughAtBats_TheOutfieldPlaysTheUsualDepth()
    {
        Assert.Equal(0, FieldGeometry.OutfieldDepthFor(new BatterStats()));
        Assert.Equal(0, FieldGeometry.OutfieldDepthFor(Stats(30, 12, 3, 0, 5)));
    }

    [Fact]
    public void OnlyTheOutfieldersMove_AlongTheLineFromHome_AndStayInsideTheWall()
    {
        var park = Stadium.Generic;
        var usual = FieldGeometry.FielderSpots(DefensiveAlignment.Standard, BatHand.Right, park);

        var deeper = FieldGeometry.WithOutfieldDepth(usual, 12, park);
        var shallower = FieldGeometry.WithOutfieldDepth(usual, -10, park);

        foreach (var pos in FieldGeometry.Outfielders)
        {
            Assert.Equal(usual[pos].DistanceFromHome + 12, deeper[pos].DistanceFromHome, 6);
            Assert.Equal(usual[pos].DistanceFromHome - 10, shallower[pos].DistanceFromHome, 6);
            Assert.Equal(usual[pos].AngleDegrees, deeper[pos].AngleDegrees, 6);
        }
        foreach (var pos in new[] { Position.P, Position.C, Position.First, Position.Second, Position.Short, Position.Third })
        {
            Assert.Equal(usual[pos], deeper[pos]);
            Assert.Equal(usual[pos], shallower[pos]);
        }
    }

    [Fact]
    public void InASmallPark_APowerHittersOutfieldStillStaysInsideTheWall()
    {
        var small = new Stadium { LeftField = 250, LeftCenter = 260, Center = 270, RightCenter = 260, RightField = 250 };
        var usual = FieldGeometry.FielderSpots(DefensiveAlignment.Standard, BatHand.Right, small);

        var deeper = FieldGeometry.WithOutfieldDepth(usual, 12, small);

        foreach (var pos in FieldGeometry.Outfielders)
            Assert.True(deeper[pos].DistanceFromHome <= FieldGeometry.WallDistance(small, deeper[pos].AngleDegrees) - 14 + 1e-9, pos.ToString());
    }

    private static PrePlaySnapshot SomeSituation()
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data", "teams"));
        var game = new GameService(new AtBatResolver(new Random(2)), new ComputerManager(new Random(2)), new Random(2));
        game.StartGame(TeamLoader.Parse(File.ReadAllText(Path.Combine(dir, "1927-nyy.json"))),
            TeamLoader.Parse(File.ReadAllText(Path.Combine(dir, "1975-cin.json"))),
            ManagerControl.Computer, ManagerControl.Computer, options: GameOptions.None);
        return PrePlaySnapshot.Capture(game.State!);
    }

    private static double CenterFielderDistance(PrePlaySnapshot situation, BatterStats hitter)
    {
        var batter = new Player { Id = 900, Name = "Test Hitter", BatHand = BatHand.Right, Overall = hitter };
        var script = PlayChoreographer.BuildSetup(situation with { Batter = batter, BatsFrom = BatHand.Right });
        var key = script.Tracks.Single(t => t.ActorId == "fielder-Center").Keys[0];
        return new FieldPoint(key.X, key.Y).DistanceFromHome;
    }

    [Fact]
    public void TheSituationPicture_PutsThePowerHittersCenterFielderDeeperThanTheSlapHittersAndTheRestUnchanged()
    {
        var situation = SomeSituation();

        double slugger = CenterFielderDistance(situation, Slugger);
        double typical = CenterFielderDistance(situation, Typical);
        double slapper = CenterFielderDistance(situation, Slapper);

        Assert.True(slugger > typical + 10, $"{slugger} vs {typical}");
        Assert.True(typical > slapper + 5, $"{typical} vs {slapper}");

        // The infield does not change with the hitter.
        FieldPoint Short(BatterStats hitter)
        {
            var batter = new Player { Id = 900, Name = "Test Hitter", BatHand = BatHand.Right, Overall = hitter };
            var key = PlayChoreographer.BuildSetup(situation with { Batter = batter, BatsFrom = BatHand.Right })
                .Tracks.Single(t => t.ActorId == "fielder-Short").Keys[0];
            return new FieldPoint(key.X, key.Y);
        }
        Assert.Equal(Short(Slugger), Short(Slapper));
    }
}
