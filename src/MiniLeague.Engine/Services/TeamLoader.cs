using System.Text.Json;
using System.Text.Json.Serialization;
using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Services;

/// <summary>
/// Builds <see cref="Team"/> objects from roster JSON (one file per team).
/// Batters and pitchers use compact stat lines; per-player ratings not present in
/// the file get the defaults below. Batting order is the first nine entries of "batters"; any further batters are the bench.
/// </summary>
public static class TeamLoader
{
    private const int LineupSize = 9;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Team Parse(string json)
    {
        var data = JsonSerializer.Deserialize<TeamData>(json, Options)
                   ?? throw new JsonException("Team file is empty.");
        return ToTeam(data);
    }

    /// <summary>Reads the index file ({ "teams": ["teams/a.json", ...] }).</summary>
    public static IReadOnlyList<string> ParseIndex(string json) =>
        JsonSerializer.Deserialize<TeamIndex>(json, Options)?.Teams ?? [];

    private static Team ToTeam(TeamData d)
    {
        var roster = new List<Player>();
        roster.AddRange(d.Batters.Select(ToBatter));
        roster.AddRange(d.Pitchers.Select(ToPitcher));

        return new Team
        {
            Id = d.Id, City = d.City, Nickname = d.Nickname, Abbreviation = d.Abbreviation,
            Season = d.Season, League = d.League,
            StadiumId = d.Stadium ?? "generic",
            EraErrorsPerGame = d.EraErrorsPerGame ?? 0.8,
            EraStrikeoutsPerGame = d.EraStrikeoutsPerGame ?? 4.0,
            IsHistorical = true,
            Roster = roster,
            DefaultLineup = roster.Where(p => !p.IsPitcher).Take(LineupSize).Select(p => p.Id).ToList(),
        };
    }

    private static Player ToBatter(BatterData b)
    {
        var stats = new BatterStats
        {
            AtBats = b.Ab, Hits = b.H, HomeRuns = b.Hr, Walks = b.Bb,
            Strikeouts = b.So, Doubles = b.D2, Triples = b.D3, StolenBases = b.Sb,
            RBI = b.Rbi, CaughtStealing = b.Cs, HitByPitch = b.Hbp,
        };
        return new Player
        {
            Id = b.Id, Name = b.Name, BatHand = b.Bats, ThrowHand = b.Throws,
            PrimaryPosition = ParsePosition(b.Pos),
            SpeedRating = b.Speed,
            PowerRating = b.Hr >= 30 ? 4 : b.Hr >= 15 ? 3 : b.Hr >= 5 ? 2 : 1,
            FieldingRating = 2,
            Fielding = ParseFielding(b),
            HasFieldingData = b.Field is not null,
            Overall = stats, VsLeft = stats, VsRight = stats,
        };
    }

    private static Player ToPitcher(PitcherData p)
    {
        var stats = new PitcherStats
        {
            ERA = p.Era, InningsPitched = p.Ip, HitsAllowed = p.H,
            WalksAllowed = p.Bb, Strikeouts = p.So,
            Wins = p.Wins, Losses = p.Losses, Saves = p.Saves,
            CompleteGames = p.Cg, HomeRunsAllowed = p.Hr,
            GroundBallRate = p.GbRate, TiringFactor = p.TiringFactor,
        };
        return new Player
        {
            Id = p.Id, Name = p.Name, ThrowHand = p.Throws,
            PrimaryPosition = Position.P, Role = p.Role,
            PitchingOverall = stats, PitchingVsLeft = stats, PitchingVsRight = stats,
        };
    }

    // "field": { "SS": [range, errorRating, arm] } — positions he has played
    private static Dictionary<Position, FieldingProfile> ParseFielding(BatterData b)
    {
        var result = new Dictionary<Position, FieldingProfile>();
        foreach (var (key, v) in b.Field ?? [])
        {
            if (v.Length != 3)
                throw new JsonException($"{b.Name}: fielding for {key} must be [range, errorRating, arm].");
            result[ParsePosition(key)] = new FieldingProfile(v[0], v[1], v[2]);
        }
        return result;
    }

    private static Position ParsePosition(string s) => s.ToUpperInvariant() switch
    {
        "C"  => Position.C, "1B" => Position.First, "2B" => Position.Second,
        "3B" => Position.Third, "SS" => Position.Short,
        "LF" => Position.Left, "CF" => Position.Center, "RF" => Position.Right,
        "OF" => Position.Left, _ => Position.DH
    };

    // ── JSON shapes ──────────────────────────────────────────────────────────

    private sealed class TeamIndex { public List<string> Teams { get; set; } = []; }

    private sealed class TeamData
    {
        public int Id { get; set; }
        public string City { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Abbreviation { get; set; } = "";
        public int Season { get; set; }
        public string League { get; set; } = "";
        public string? Stadium { get; set; }
        public double? EraErrorsPerGame { get; set; }
        public double? EraStrikeoutsPerGame { get; set; }
        public List<BatterData> Batters { get; set; } = [];
        public List<PitcherData> Pitchers { get; set; } = [];
    }

    private sealed class BatterData
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Pos { get; set; } = "DH";
        public BatHand Bats { get; set; } = BatHand.Right;
        public ThrowHand Throws { get; set; } = ThrowHand.Right;
        public int Ab { get; set; }
        public int H { get; set; }
        public int Hr { get; set; }
        public int Bb { get; set; }
        public int So { get; set; }
        public int D2 { get; set; }
        public int D3 { get; set; }
        public int Sb { get; set; }
        public int Rbi { get; set; }
        public int Cs { get; set; }
        public int Hbp { get; set; }
        public int Speed { get; set; } = 3;
        public Dictionary<string, int[]>? Field { get; set; }
    }

    private sealed class PitcherData
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ThrowHand Throws { get; set; } = ThrowHand.Right;
        public double Era { get; set; }
        public double Ip { get; set; }
        public int H { get; set; }
        public int Bb { get; set; }
        public int So { get; set; }
        public double GbRate { get; set; } = 0.45;
        public int TiringFactor { get; set; } = 5;
        public PitcherRole Role { get; set; } = PitcherRole.Starter;
        public int Wins { get; set; } = 15;
        public int Losses { get; set; } = 8;
        public int Saves { get; set; }
        public int Cg { get; set; }
        public int Hr { get; set; }
    }
}
