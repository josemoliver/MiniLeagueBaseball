using Microsoft.JSInterop;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;

namespace MiniLeague.Web.Services;

/// <summary>Holds one game in progress so it can be resumed later.</summary>
public interface IGameSaveStore
{
    Task SaveAsync(GameState state);
    /// <summary>The saved game, or null if there is none or it cannot be read.</summary>
    Task<GameSnapshot?> LoadAsync();
    Task ClearAsync();
}

/// <summary>
/// Keeps the game in the browser's localStorage so it survives a refresh.
/// Best-effort: if storage is unavailable the game still plays, it just cannot be resumed.
/// </summary>
public class GameSaveStore(IJSRuntime js) : IGameSaveStore
{
    private const string Key = "mlb-saved-game";

    public async Task SaveAsync(GameState state)
    {
        try
        {
            if (state.Status == GameStatus.Final)
                await ClearAsync();
            else
                await js.InvokeAsync<bool>("mlbStorage.set", Key, GameSnapshots.ToJson(GameSnapshots.Capture(state)));
        }
        catch (JSException) { }
    }

    public async Task<GameSnapshot?> LoadAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("mlbStorage.get", Key);
            return string.IsNullOrEmpty(json) ? null : GameSnapshots.FromJson(json);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async Task ClearAsync()
    {
        try { await js.InvokeVoidAsync("mlbStorage.remove", Key); }
        catch (JSException) { }
    }
}
