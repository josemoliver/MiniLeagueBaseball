using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;
using MiniLeague.SurveyTool;

namespace MiniLeague.Engine.Tests;

public class SurveyToolTests
{
    private static readonly Lazy<SurveyResult> Small = new(() => SurveyRunner.Run(
        new SurveyOptions(GamesPerMatchup: 1, Seed: 7, Dh: SurveyDhRule.Designated, MatchupLimit: 1)));

    [Fact]
    public void SurveyWritesBothSidesAndRepeatsExactly()
    {
        var result = Small.Value;
        Assert.Single(result.Games);
        Assert.True(result.Rows.Count > 0);
        Assert.Equal(result.Games[0].Turns * 2, result.Rows.Count);
        Assert.All(result.Rows.Chunk(2), pair =>
        {
            Assert.Equal("Batting", pair[0].Side);
            Assert.Equal("Fielding", pair[1].Side);
            Assert.Equal(pair[0].Seed, pair[1].Seed);
            Assert.Equal(pair[0].Turn, pair[1].Turn);
        });

        var repeat = SurveyRunner.Run(result.Options);
        Assert.Equal(SurveyReport.RowsCsv(result), SurveyReport.RowsCsv(repeat));
        Assert.Equal(SurveyReport.GroupsCsv(result), SurveyReport.GroupsCsv(repeat));
        Assert.Equal(SurveyReport.Summary(result), SurveyReport.Summary(repeat));
        Assert.All(result.Rows, row => Assert.False(string.IsNullOrWhiteSpace(row.LeveragePhrase)));
        Assert.Contains("## Leverage phrases", SurveyReport.Summary(result));

        string firstDir = Directory.CreateTempSubdirectory("mini-survey-a-").FullName;
        string secondDir = Directory.CreateTempSubdirectory("mini-survey-b-").FullName;
        try
        {
            SurveyReport.Write(result, firstDir);
            SurveyReport.Write(repeat, secondDir);
            foreach (var file in new[] { "rows.csv", "groups.csv", "summary.md" })
                Assert.Equal(File.ReadAllBytes(Path.Combine(firstDir, file)), File.ReadAllBytes(Path.Combine(secondDir, file)));
        }
        finally
        {
            Directory.Delete(firstDir, recursive: true);
            Directory.Delete(secondDir, recursive: true);
        }
    }

    [Fact]
    public void SeedsIncludeTheDhRuleAndGameNumber()
    {
        int designated = SurveyRunner.SeedFor(1, 0, 1, 0, 0, 15);
        int historical = SurveyRunner.SeedFor(1, 0, 1, 1, 0, 15);
        int pitchers = SurveyRunner.SeedFor(1, 0, 1, 2, 0, 15);
        int nextGame = SurveyRunner.SeedFor(1, 0, 1, 0, 1, 15);
        Assert.Equal(4, new[] { designated, historical, pitchers, nextGame }.Distinct().Count());
    }

    [Fact]
    public void HintAuditLeavesTheGameUnchanged()
    {
        var ordinary = Small.Value;
        var hinted = SurveyRunner.Run(ordinary.Options with { Hints = true });
        Assert.Equal(ordinary.Games, hinted.Games);
        Assert.All(hinted.Rows, row =>
        {
            Assert.NotEmpty(row.HintReasonId);
            Assert.DoesNotMatch(@"\d", row.HintText);
        });
        Assert.Contains("## Hint wording audit", SurveyReport.Summary(hinted));
    }

    [Fact]
    public void ObservationDoesNotChangeTheGameResult()
    {
        var surveyed = Small.Value.Games[0];
        string dir = Path.Combine(FindRepoRoot(), "src", "MiniLeague.Web", "wwwroot", "data", "teams");
        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var visitor = TeamLoader.Parse(File.ReadAllText(files[0]));
        var home = TeamLoader.Parse(File.ReadAllText(files[1]));
        var game = new GameService(new AtBatResolver(new Random(surveyed.Seed)),
            new ComputerManager(new Random(surveyed.Seed)), new Random(surveyed.Seed),
            ManagerCatalog.Default, managerSeed: surveyed.Seed);
        game.StartGame(visitor, home, ManagerControl.Computer, ManagerControl.Computer,
            options: GameOptions.None with { DesignatedHitter = surveyed.Rule },
            visitorManagerId: "analyst", homeManagerId: "analyst");
        int turns = 0;
        while (game.State!.Status == GameStatus.InProgress)
        {
            Assert.True(++turns <= 4000);
            Assert.NotNull(game.ExecuteComputerTurn());
        }
        Assert.Equal(surveyed.Turns, turns);
        Assert.Equal(surveyed.VisitorRuns, game.State.VisitorTotalScore);
        Assert.Equal(surveyed.HomeRuns, game.State.HomeTotalScore);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MiniLeagueBaseball.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
