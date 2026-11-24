using MiniLeague.Engine.Models;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

public class TeamLoaderTests
{
    private const string Json = """
    {
      "id": 7, "city": "Test", "nickname": "Nines", "abbreviation": "TST", "season": 1999, "league": "NL",
      "batters": [
        { "id": 1, "name": "A. Hitter", "pos": "SS", "bats": "Switch", "throws": "Right",
          "ab": 500, "h": 150, "hr": 31, "bb": 60, "so": 80, "d2": 20, "d3": 3, "sb": 10, "speed": 4 }
      ],
      "pitchers": [
        { "id": 2, "name": "B. Arm", "throws": "Left", "era": 3.25, "ip": 200, "h": 190, "bb": 50, "so": 150,
          "role": "Closer" }
      ]
    }
    """;

    [Fact]
    public void Parse_BuildsTeamWithBattersPitchersAndLineup()
    {
        var team = TeamLoader.Parse(Json);

        Assert.Equal("1999 Test Nines", team.DisplayName);
        Assert.Equal([1], team.DefaultLineup);

        var batter = Assert.Single(team.Batters);
        Assert.Equal(Position.Short, batter.PrimaryPosition);
        Assert.Equal(BatHand.Switch, batter.BatHand);
        Assert.Equal(4, batter.PowerRating);
        Assert.Equal(150, batter.Overall!.Hits);

        var pitcher = Assert.Single(team.Pitchers);
        Assert.Equal(PitcherRole.Closer, pitcher.Role);
        Assert.Equal(ThrowHand.Left, pitcher.ThrowHand);
        Assert.Equal(3.25, pitcher.PitchingOverall!.ERA);
        Assert.Equal(5, pitcher.PitchingOverall.TiringFactor);  // default
    }

    [Fact]
    public void Parse_LineupIsFirstNineBatters_RestAreBench()
    {
        var batters = string.Join(",", Enumerable.Range(1, 12).Select(i =>
            $$"""{ "id": {{i}}, "name": "P{{i}}", "pos": "DH", "ab": 100, "h": 25 }"""));
        var team = TeamLoader.Parse(
            $$"""{ "id": 1, "batters": [{{batters}}], "pitchers": [] }""");

        Assert.Equal(Enumerable.Range(1, 9), team.DefaultLineup);
        Assert.Equal(12, team.Batters.Count());
    }

    [Fact]
    public void ParseIndex_ReturnsTeamPaths() =>
        Assert.Equal(["teams/a.json", "teams/b.json"],
            TeamLoader.ParseIndex("""{ "teams": ["teams/a.json", "teams/b.json"] }"""));

    [Fact]
    public void ShippedTeamFiles_AllParse()
    {
        var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));
        var index = TeamLoader.ParseIndex(File.ReadAllText(Path.Combine(dataDir, "teams.json")));

        Assert.NotEmpty(index);
        foreach (var path in index)
        {
            var team = TeamLoader.Parse(File.ReadAllText(Path.Combine(dataDir, path)));
            Assert.True(team.DefaultLineup.Count == 9, $"{path}: needs a 9-man lineup");
            Assert.Equal(9, team.DefaultLineup.Distinct().Count());
            Assert.NotEmpty(team.Starters);
        }
    }

    [Fact]
    public void ShippedTeams_PlayCompleteGamesWithoutErrors()
    {
        var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));
        var teams = TeamLoader.ParseIndex(File.ReadAllText(Path.Combine(dataDir, "teams.json")))
            .Select(path => TeamLoader.Parse(File.ReadAllText(Path.Combine(dataDir, path))))
            .ToList();

        var rng = new Random(42);
        for (int i = 0; i < teams.Count; i++)
        {
            var visitor = teams[i];
            var home = teams[(i + 1) % teams.Count];
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng));
            game.StartGame(visitor, home, ManagerControl.Computer, ManagerControl.Computer);

            for (int plays = 0; game.State!.Status == GameStatus.InProgress; plays++)
            {
                Assert.True(plays < 3000, $"{visitor.DisplayName} @ {home.DisplayName}: game never ended");
                game.ExecuteComputerTurn();
            }
        }
    }
}
