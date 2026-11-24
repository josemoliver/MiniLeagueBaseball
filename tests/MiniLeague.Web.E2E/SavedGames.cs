using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Web.E2E;

/// <summary>Saved games built with the real engine, to start a test from a place that takes minutes to reach by playing.</summary>
public static class SavedGames
{
    /// <summary>A computer-managed close game where a runner at third calls for the infield to move in before play.</summary>
    public static string ComputerInfieldIn()
    {
        string teams = Path.Combine(AppFixture.FindRepoRoot(), "src", "MiniLeague.Web", "wwwroot", "data", "teams");
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(teams, file)));
        var game = new GameService(new AtBatResolver(new Random(7)), new ComputerManager(new Random(7)), new Random(7));
        game.StartGame(Load("1927-nyy.json"), Load("1955-bro.json"), ManagerControl.Computer, ManagerControl.Computer);
        var state = game.State!;
        state.RunnerOnThird = new BaseRunner { Player = state.LineupPlayers(true).ElementAt(1).Player, BattingSlot = 1 };
        return GameSnapshots.ToJson(GameSnapshots.Capture(state));
    }

    /// <summary>
    /// A game with the computer on both sides, saved one play before it ends. Resume it and the very next play
    /// finishes the game. (The browser's own save format, so it goes straight into storage.)
    /// </summary>
    public static string OnePlayFromTheEnd(int seed = 11)
    {
        string teams = Path.Combine(AppFixture.FindRepoRoot(), "src", "MiniLeague.Web", "wwwroot", "data", "teams");
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(teams, file)));

        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1955-bro.json"), ManagerControl.Computer, ManagerControl.Computer);

        // WHY: serialized to JSON after every play, not captured as an object: Capture shares the live box score with
        // the game (it does not copy it), so an object kept from an earlier play would show later plays too.
        string beforeThisPlay = GameSnapshots.ToJson(GameSnapshots.Capture(game.State!));
        for (int plays = 0; plays < 3000; plays++)
        {
            game.ExecuteComputerTurn();
            if (game.State!.Status == GameStatus.Final) return beforeThisPlay;
            beforeThisPlay = GameSnapshots.ToJson(GameSnapshots.Capture(game.State));
        }
        throw new InvalidOperationException("The game never ended.");
    }
}
