using System.Text.Json;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The chance a steal succeeds follows the runner's record, steadied toward his own league's rate, and matches the real teams.</summary>
public class StealCalibrationTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string TeamsDir => Path.Combine(RepoRoot, "src", "MiniLeague.Web", "wwwroot", "data", "teams");

    private static Player Runner(int sb, int cs, double leagueRate) => new()
    { SpeedRating = 3, LeagueStealRate = leagueRate, Overall = new BatterStats { StolenBases = sb, CaughtStealing = cs } };

    private static double Chance(Player runner) => BaseRunningEngine.StealSuccessChance(runner, catcherArm: 3, holdRunners: 3, pitchout: false);

    [Fact]
    public void ARunnerWithNoRecordSucceedsAtHisLeaguesRate()
    {
        Assert.Equal(0.58, Chance(Runner(0, 0, 0.58)), 3);
        Assert.Equal(0.68, Chance(Runner(0, 0, 0.68)), 3);
    }

    [Fact]
    public void ALongRecordOutweighsTheLeaguesRate()
    {
        // 40 for 50 is 80%. A league that stole at 58% pulls it down only a little (five attempts of weight against fifty).
        double chance = Chance(Runner(40, 10, 0.58));

        Assert.InRange(chance, 0.76, 0.80);
        Assert.True(Chance(Runner(40, 10, 0.68)) > chance);   // a higher league rate lifts him, but only a little
    }

    [Fact]
    public void AShortRecordMovesTowardTheLeagueRateMoreThanALongOne()
    {
        double shortGap = Math.Abs(Chance(Runner(3, 0, 0.60)) - 1.0);   // 3 for 3 against a league at 60%
        double longGap = Math.Abs(Chance(Runner(30, 0, 0.60)) - 1.0);

        Assert.True(shortGap > longGap);
    }

    [Fact]
    public void EveryShippedTeamCarriesItsLeaguesStealRate_AndItsRunnersAreSteadiedByIt()
    {
        foreach (var file in Directory.GetFiles(TeamsDir, "*.json"))
        {
            var team = TeamLoader.Parse(File.ReadAllText(file));

            Assert.InRange(team.LeagueStealRate, 0.5, 0.75);
            Assert.NotEqual(RunningRules.DefaultLeagueStealRate, team.LeagueStealRate);
            Assert.All(team.Roster.Where(p => !p.IsPitcher), p => Assert.Equal(team.LeagueStealRate, p.LeagueStealRate));
        }
    }

    [Fact]
    public void ATeamFileWithoutALeagueStealRate_UsesTheDefault()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(TeamsDir, "*.json").First()));
        var withoutRate = JsonSerializer.Serialize(
            doc.RootElement.EnumerateObject().Where(p => p.Name != "leagueStealRate").ToDictionary(p => p.Name, p => p.Value));

        var team = TeamLoader.Parse(withoutRate);

        Assert.Equal(RunningRules.DefaultLeagueStealRate, team.LeagueStealRate);
    }

    [Fact]
    public void WeightedByAttempts_TheModelReproducesEachRealTeamsSuccessRate()
    {
        // WHY: against an average battery, the success the model gives the runners who actually stole, weighted by how often each
        // did, is what the real team achieved. It is a check of the formula alone, with no manager choosing who goes.
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot, "tools", "MiniLeague.ManagerBench", "lahman-reference.json")));
        var errors = new List<double>();
        foreach (var team in doc.RootElement.GetProperty("teams").EnumerateArray())
        {
            string id = team.GetProperty("id").GetString()!;
            var loaded = TeamLoader.Parse(File.ReadAllText(Path.Combine(TeamsDir, id + ".json")));
            double expected = 0, attempts = 0;
            foreach (var p in loaded.Roster.Where(p => !p.IsPitcher && p.Overall is { } o && o.StolenBases + o.CaughtStealing > 0))
            {
                double n = p.Overall!.StolenBases + p.Overall.CaughtStealing;
                expected += n * Chance(p);
                attempts += n;
            }
            double real = team.GetProperty("rates").GetProperty("stealSuccess").GetDouble();
            errors.Add(expected / attempts - real);
            Assert.InRange(expected / attempts - real, -0.05, 0.12);   // the 1968 Tigers (45%) are the outlier, a short-record team
        }

        Assert.True(errors.Average() is > -0.01 and < 0.04, $"mean error {errors.Average():F3}");
        Assert.True(errors.Select(Math.Abs).Average() < 0.04, $"mean absolute error {errors.Select(Math.Abs).Average():F3}");
    }
}
