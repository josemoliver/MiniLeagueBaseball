using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.ManagerBench;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// Calibration compares with a real season, which a whole rotation pitched. Starting the ace every game made every opponent's
/// pitching far better than it was, and showed as an engine that walked too few and struck out too many.
/// </summary>
public class CalibrationRotationTests
{
    private static string TeamsDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data", "teams"));

    [Fact]
    public void StartersTakeTurnsInProportionToTheGamesTheyReallyStarted()
    {
        var team = TeamLoader.Parse(File.ReadAllText(Path.Combine(TeamsDir, "1970-bal.json")));
        var rotation = team.Pitchers.Where(p => p.Role == PitcherRole.Starter).ToList();
        double total = rotation.Sum(p => p.PitchingOverall!.GamesStarted);
        var starts = new Dictionary<string, Dictionary<int, int>>();

        const int games = 400;
        var taken = Enumerable.Range(0, games).Select(_ => Calibration.NextStarter(team, "1970-bal", starts)).ToList();

        Assert.All(rotation, p => Assert.InRange(taken.Count(id => id == p.Id) / (double)games,
            p.PitchingOverall!.GamesStarted / total - 0.01, p.PitchingOverall!.GamesStarted / total + 0.01));
        // and it is a rotation from the start, not the ace over and over
        Assert.True(taken.Take(rotation.Count).Distinct().Count() >= rotation.Count - 1);
    }

    [Fact]
    public void ATeamFileWithoutGamesStarted_SharesTheStartsEvenly()
    {
        var team = TeamLoader.Parse(File.ReadAllText(Path.Combine(TeamsDir, "1970-bal.json")));
        var rotation = team.Pitchers.Where(p => p.Role == PitcherRole.Starter).ToList();
        foreach (var p in rotation) p.PitchingOverall!.GamesStarted = 0;
        var starts = new Dictionary<string, Dictionary<int, int>>();

        var taken = Enumerable.Range(0, rotation.Count * 10).Select(_ => Calibration.NextStarter(team, "x", starts)).ToList();

        Assert.All(rotation, p => Assert.Equal(10, taken.Count(id => id == p.Id)));
    }
}
