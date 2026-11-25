using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// Pitches are only estimated, but fatigue and the managers count them, so the estimate has to be plausible: about 3.5 a plate
/// appearance, with strikeouts and walks running long. At about 4.0 nine innings took some 150 pitches and a durable starter
/// could throw 180.
/// </summary>
public class PitchEstimateTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data", "teams"));

    private static List<PlayResult> Plays()
    {
        var plays = new List<PlayResult>();
        for (int seed = 1; seed <= 40; seed++)
        {
            var game = new GameService(new AtBatResolver(new Random(seed)), new MiniLeague.Engine.AI.ComputerManager(new Random(seed)),
                new Random(seed));
            game.StartGame(TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "1975-cin.json"))),
                TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "1927-nyy.json"))),
                ManagerControl.Computer, ManagerControl.Computer);
            for (int g = 0; g < 2000 && game.ExecuteComputerTurn() is { } play; g++) plays.Add(play);
        }
        return plays;
    }

    [Fact]
    public void APlateAppearanceTakesAboutThreeAndAHalfPitches_WithStrikeoutsAndWalksLonger()
    {
        var plays = Plays().Where(p => p.Outcome is not (OutcomeType.StolenBase or OutcomeType.CaughtStealing
            or OutcomeType.IntentionalWalk)).ToList();
        double Average(Func<PlayResult, bool> which) => plays.Where(which).Average(p => p.PitchesThrown);

        Assert.InRange(Average(_ => true), 3.3, 3.7);
        double inPlay = Average(p => p.Outcome is OutcomeType.GroundOut or OutcomeType.FlyOut or OutcomeType.Single);
        Assert.True(Average(p => p.Outcome == OutcomeType.Strikeout) > inPlay + 1.5);
        Assert.True(Average(p => p.Outcome == OutcomeType.Walk) > inPlay + 2);
        Assert.All(plays, p => Assert.InRange(p.PitchesThrown, 1, 7));
    }

    [Fact]
    public void TheStaminaFormulaUsesTheEnginesOwnPitchesPerOut()
    {
        var plays = Plays();
        double perOut = plays.Sum(p => (double)p.PitchesThrown) / plays.Sum(p => p.OutsRecorded);

        Assert.InRange(perOut, FatigueModel.PitchesPerOut - 0.3, FatigueModel.PitchesPerOut + 0.3);
    }
}
