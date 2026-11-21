using System.Text.Json;
using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Services;

/// <summary>Reads ballparks from JSON: <c>{ "stadiums": [ { "id": "...", "name": "...", ... } ] }</c>.</summary>
public static class StadiumLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<Stadium> ParseAll(string json)
    {
        var file = JsonSerializer.Deserialize<StadiumFile>(json, Options)
                   ?? throw new JsonException("Stadium file is empty.");
        return file.Stadiums.Select(ToStadium).ToList();
    }

    private static Stadium ToStadium(StadiumData d)
    {
        if (string.IsNullOrWhiteSpace(d.Id)) throw new JsonException("Every stadium needs an id.");
        return new Stadium
        {
            Id = d.Id, Name = d.Name, City = d.City,
            LeftField = d.Left, LeftCenter = d.LeftCenter, Center = d.Center,
            RightCenter = d.RightCenter, RightField = d.Right,
            LeftWallHeight = d.LeftWall, RightWallHeight = d.RightWall,
            HomeRunFactorLeft = d.HrLeft, HomeRunFactorRight = d.HrRight,
            DoublesTriplesFactor = d.DoublesTriples,
            SinglesFactor = d.Singles,
            RunsFactor = d.Runs,
            IsTurf = d.Turf, IsDome = d.Dome,
        };
    }

    private sealed class StadiumFile { public List<StadiumData> Stadiums { get; set; } = []; }

    private sealed class StadiumData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
        public int Left { get; set; } = 330;
        public int LeftCenter { get; set; } = 375;
        public int Center { get; set; } = 400;
        public int RightCenter { get; set; } = 375;
        public int Right { get; set; } = 330;
        public int LeftWall { get; set; } = 8;
        public int RightWall { get; set; } = 8;
        public double HrLeft { get; set; } = 1.0;
        public double HrRight { get; set; } = 1.0;
        public double DoublesTriples { get; set; } = 1.0;
        public double Singles { get; set; } = 1.0;
        public double Runs { get; set; } = 1.0;
        public bool Turf { get; set; }
        public bool Dome { get; set; }
    }
}
