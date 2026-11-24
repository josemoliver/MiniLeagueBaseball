using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>Choosing who starts the game on the mound.</summary>
public class StartingPitcherTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService Start(Team visitor, Team home, int? visitorStarter = null, int? homeStarter = null, int seed = 1)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(visitor, home, ManagerControl.Computer, ManagerControl.Computer,
            visitorStarterId: visitorStarter, homeStarterId: homeStarter);
        return game;
    }

    [Fact]
    public void WithoutAChoice_TheAceStarts()
    {
        var visitor = Load("1927-nyy.json");
        var s = Start(visitor, Load("1975-bos.json")).State!;

        Assert.Equal(visitor.Starters.First().Id, s.VisitorPitcher.Player.Id);
        Assert.Equal(visitor.DefaultStarter.Id, s.VisitorPitcher.Player.Id);
    }

    [Fact]
    public void ThePitcherChosenStarts_ForEachSideSeparately()
    {
        var visitor = Load("1927-nyy.json");
        var home = Load("1975-bos.json");
        var third = visitor.Starters.ElementAt(2);
        var second = home.Starters.ElementAt(1);

        var s = Start(visitor, home, third.Id, second.Id).State!;

        Assert.Equal(third.Id, s.VisitorPitcher.Player.Id);
        Assert.Equal(second.Id, s.HomePitcher.Player.Id);
        Assert.Equal(third.Id, s.Box.Visitor.Pitchers[0].PlayerId);   // and he is the one in the box score
        Assert.Contains(third.Id, s.VisitorUsed);
    }

    [Fact]
    public void TheBullpenIsStillTheRelievers_WhoeverStarts()
    {
        var visitor = Load("1927-nyy.json");
        var ace = Start(visitor, Load("1975-bos.json")).State!;
        var other = Start(visitor, Load("1975-bos.json"), visitor.Starters.Last().Id).State!;

        Assert.Equal(ace.VisitorBullpen.Select(w => w.Player.Id).Order(), other.VisitorBullpen.Select(w => w.Player.Id).Order());
        Assert.DoesNotContain(other.VisitorBullpen, w => w.Player.Id == visitor.Starters.Last().Id);
    }

    [Fact]
    public void AReliefPitcherCanStart_AndLeavesTheBullpen()
    {
        var visitor = Load("1927-nyy.json");
        var reliever = visitor.Bullpen.First();

        var s = Start(visitor, Load("1975-bos.json"), reliever.Id).State!;

        Assert.Equal(reliever.Id, s.VisitorPitcher.Player.Id);
        Assert.DoesNotContain(s.VisitorBullpen, w => w.Player.Id == reliever.Id);
        Assert.Equal(visitor.Bullpen.Count() - 1, s.VisitorBullpen.Count);
    }

    [Fact]
    public void OnlyAPitcherOnThatTeamCanStart()
    {
        var visitor = Load("1927-nyy.json");
        var home = Load("1975-bos.json");
        var hitter = visitor.Batters.First();
        var otherTeamsPitcher = home.Starters.First();

        Assert.Throws<ArgumentException>(() => Start(visitor, home, hitter.Id));
        Assert.Throws<ArgumentException>(() => Start(visitor, home, otherTeamsPitcher.Id));
        Assert.Throws<ArgumentException>(() => Start(visitor, home, homeStarter: 424242));
    }

    [Fact]
    public void TheChoicesListTheRotationFirstThenTheBullpen()
    {
        var team = Load("1927-nyy.json");

        var choices = team.StartingPitcherChoices.ToList();

        Assert.Equal(team.Pitchers.Count(), choices.Count);
        Assert.Equal(choices.Count, choices.Select(p => p.Id).Distinct().Count());
        Assert.All(choices.Take(4), p => Assert.Equal(PitcherRole.Starter, p.Role));
        Assert.Equal(team.DefaultStarter.Id, choices[0].Id);
    }

    [Fact]
    public void AGameWithAnyChosenStarterPlaysToTheEnd()
    {
        var visitor = Load("1927-nyy.json");
        var home = Load("1975-bos.json");
        foreach (var starter in visitor.StartingPitcherChoices)
        {
            var game = Start(visitor, home, starter.Id, seed: starter.Id);
            var s = game.State!;
            for (int plays = 0; s.Status == GameStatus.InProgress; plays++)
            {
                Assert.True(plays < 4000);
                game.ExecuteComputerTurn();
            }
            Assert.Equal(starter.Id, s.Box.Visitor.Pitchers[0].PlayerId);
        }
    }

    [Fact]
    public void AWeakerStarterIsRelievedMoreOften_SoPickingOneChangesTheGame()
    {
        var visitor = Load("1927-nyy.json");
        var home = Load("1975-bos.json");
        var worst = visitor.Starters.OrderByDescending(p => p.PitchingOverall!.ERA).First();

        double ChangesPerGame(Player starter)
        {
            int changes = 0;
            for (int seed = 0; seed < 80; seed++)
            {
                var game = Start(visitor, home, starter.Id, seed: seed);
                var s = game.State!;
                while (s.Status == GameStatus.InProgress) game.ExecuteComputerTurn();
                changes += s.Box.Visitor.Pitchers.Count - 1;
            }
            return changes / 80.0;
        }

        Assert.True(ChangesPerGame(worst) > ChangesPerGame(visitor.DefaultStarter),
            "the worst starter should be pulled more often than the ace");
    }
}
