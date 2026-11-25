using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>A starter tires by how long his real starts were, not by a whole-innings factor that put nearly all of them together.</summary>
public class StaminaTests
{
    private static string TeamsDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data", "teams"));

    private static ActivePitcher Pitching(PitcherStats stats, int pitches) => new()
    {
        Player = new Player { PitchingOverall = stats, Role = PitcherRole.Starter }, PitchCount = pitches, BattersFaced = 20,
    };

    // Seven innings a start, none finished, and the same man who finishes half of them.
    private static PitcherStats Starter(double outsPerStart, int starts, int completeGames) => new()
    { InningsPitched = outsPerStart / 3 * starts, GamesStarted = starts, CompleteGames = completeGames, TiringFactor = 3 };

    [Fact]
    public void AStarterWhoGoesDeeperTiresLater()
    {
        var short_ = Starter(18, 30, 0);   // six innings
        var deep = Starter(24, 30, 0);     // eight

        Assert.True(FatigueModel.Stamina(deep) > FatigueModel.Stamina(short_) + 20);
        // at the same pitch count the short starter is the more tired
        Assert.True(FatigueModel.GetModifier(Pitching(short_, 110)) < FatigueModel.GetModifier(Pitching(deep, 110)));
    }

    [Fact]
    public void AStarterWhoFinishesGamesHasMoreInReserveThanHisAverageStart()
    {
        Assert.True(FatigueModel.Stamina(Starter(21, 30, 15)) > FatigueModel.Stamina(Starter(21, 30, 0)));
    }

    [Fact]
    public void WithoutRealStarts_TheTiringFactorStillSetsTheThresholds()
    {
        var durable = new PitcherStats { TiringFactor = 1 };
        var fragile = new PitcherStats { TiringFactor = 9 };

        Assert.Equal(0, FatigueModel.Stamina(durable));
        Assert.Equal(0, FatigueModel.Stamina(null));
        Assert.True(FatigueModel.GetModifier(Pitching(durable, 100)) > FatigueModel.GetModifier(Pitching(fragile, 100)));
    }

    [Fact]
    public void StaminaIsKeptToAPlausibleRange()
    {
        Assert.InRange(FatigueModel.Stamina(Starter(6, 30, 0)), 70, 150);
        Assert.Equal(140, FatigueModel.Stamina(Starter(40, 30, 30)));
    }

    [Fact]
    public void EveryShippedStarterCarriesHisGamesStarted_AndTheirStaminaDiffers()
    {
        var staminas = new List<double>();
        foreach (var file in Directory.GetFiles(TeamsDir, "*.json"))
            foreach (var p in TeamLoader.Parse(File.ReadAllText(file)).Roster.Where(p => p.Role == PitcherRole.Starter))
            {
                var stats = p.PitchingOverall!;
                Assert.True(stats.GamesStarted >= 5, $"{file}: {p.Name}");
                Assert.InRange(stats.OutsPerStart, 12, 28);
                staminas.Add(FatigueModel.Stamina(stats));
            }

        // WHY: the whole point is spread. The tiring factor put almost every starter within 15 pitches of the others.
        Assert.True(staminas.Max() - staminas.Min() > 30, $"{staminas.Min():F0} to {staminas.Max():F0}");
    }
}
