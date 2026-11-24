using System.Net.Http.Json;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;

namespace MiniLeague.Web.Services;

/// <summary>
/// Loads rosters and ballparks from wwwroot/data. To add a team, drop a JSON file in
/// wwwroot/data/teams/ and list it in wwwroot/data/teams.json; ballparks live in stadiums.json.
/// </summary>
public class TeamRepository(HttpClient http)
{
    private IReadOnlyList<Team>? _teams;
    private IReadOnlyList<Stadium>? _stadiums;

    public async Task<IReadOnlyList<Team>> GetTeamsAsync()
    {
        if (_teams is not null) return _teams;

        var index = TeamLoader.ParseIndex(await http.GetStringAsync("data/teams.json"));
        var teams = new List<Team>();
        foreach (var path in index)
            teams.Add(TeamLoader.Parse(await http.GetStringAsync($"data/{path}")));

        return _teams = teams.OrderBy(t => t.Season).ThenBy(t => t.City).ToList();
    }

    public async Task<IReadOnlyList<Stadium>> GetStadiumsAsync() =>
        _stadiums ??= StadiumLoader.ParseAll(await http.GetStringAsync("data/stadiums.json"))
            .OrderBy(s => s.Name).ToList();

    /// <summary>A loaded ballpark by id, or the generic park if it is unknown (or not loaded yet).</summary>
    public Stadium FindStadium(string id) =>
        _stadiums?.FirstOrDefault(s => s.Id == id) ?? Stadium.Generic;
}
