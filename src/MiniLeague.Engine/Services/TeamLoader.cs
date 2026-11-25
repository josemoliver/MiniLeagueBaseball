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
        double leagueStealRate = d.LeagueStealRate ?? Simulation.RunningRules.DefaultLeagueStealRate;
        roster.AddRange(d.Batters.Select(b => ToBatter(b, leagueStealRate)));
        var league = d.LeaguePitching is { } lp
            ? new LeaguePitching(lp.Era, lp.NonHomeRunHitsPerNine, lp.HomeRunsPerNine, lp.StrikeoutsPerNine, lp.WalksPerNine)
            : LeaguePitching.Default;
        roster.AddRange(d.Pitchers.Select(p => ToPitcher(p, league)));

        return new Team
        {
            Id = d.Id, City = d.City, Nickname = d.Nickname, Abbreviation = d.Abbreviation,
            Season = d.Season, League = d.League,
            StadiumId = d.Stadium ?? "generic",
            EraErrorsPerGame = d.EraErrorsPerGame ?? 0.8,
            EraStrikeoutsPerGame = d.EraStrikeoutsPerGame ?? 4.0,
            LeaguePitching = league,
            LeagueStealRate = leagueStealRate,
            Tendencies = d.Tendencies is { } t
                ? new TeamTendencies(t.StealAttemptsPerGame, t.SacrificesPerGame, t.CompleteGameRate, t.SavesPerGame)
                : null,
            IsHistorical = true,
            Roster = roster,
            DefaultLineup = roster.Where(p => !p.IsPitcher).Take(LineupSize).Select(p => p.Id).ToList(),
        };
    }

    private static Player ToBatter(BatterData b, double leagueStealRate)
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
            LeagueStealRate = leagueStealRate,
            PowerRating = b.Hr >= 30 ? 4 : b.Hr >= 15 ? 3 : b.Hr >= 5 ? 2 : 1,
            // WHY: a player's sacrifice hits are the record of how well and how often he bunted. Without them (team
            // files generated before "sh" was added) he keeps the average rating of 3.
            BuntingAbility = b.Sh is int sh ? BuntRating(sh, b.Ab + b.Bb + b.Hbp + sh) : 3,
            SacrificeAbility = b.Sh is int sh2 ? BuntRating(sh2, b.Ab + b.Bb + b.Hbp + sh2) : 3,
            FieldingRating = 2,
            Fielding = ParseFielding(b),
            HasFieldingData = b.Field is not null,
            Overall = stats, VsLeft = stats, VsRight = stats,
        };
    }

    private static Player ToPitcher(PitcherData p, LeaguePitching league)
    {
        var stats = new PitcherStats
        {
            ERA = p.Era, InningsPitched = p.Ip, HitsAllowed = p.H,
            WalksAllowed = p.Bb, Strikeouts = p.So,
            Wins = p.Wins, Losses = p.Losses, Saves = p.Saves,
            CompleteGames = p.Cg, HomeRunsAllowed = p.Hr,
            GroundBallRate = p.GbRate, TiringFactor = p.TiringFactor, GamesStarted = p.Gs ?? 0,
            HoldRunners = p.HoldRunners ?? 3,
            League = league,
        };
        // WHY: with no designated hitter a pitcher has to bat. A file written by the generator carries his own batting
        // line; a hand-written one gets an ordinary pitcher's: about .150, little power, and he can bunt.
        var bat = p.Bat ?? DefaultPitcherBatting;
        var batting = new BatterStats
        {
            AtBats = bat.Ab, Hits = bat.H, Doubles = bat.D2, Triples = bat.D3, HomeRuns = bat.Hr,
            Walks = bat.Bb, Strikeouts = bat.So, HitByPitch = bat.Hbp,
        };
        int plateAppearances = bat.Ab + bat.Bb + bat.Hbp + (bat.Sh ?? 0);
        int bunting = Math.Max(4, bat.Sh is int sh ? BuntRating(sh, plateAppearances) : 4);
        return new Player
        {
            Id = p.Id, Name = p.Name, ThrowHand = p.Throws, BatHand = p.Bats ?? BatHand.Right,
            PrimaryPosition = Position.P, Role = p.Role,
            PitchingOverall = stats, PitchingVsLeft = stats, PitchingVsRight = stats,
            Overall = batting, VsLeft = batting, VsRight = batting,
            SpeedRating = 2, PowerRating = 1, BuntingAbility = bunting, SacrificeAbility = bunting,
        };
    }

    /// <summary>An ordinary pitcher at the plate, for a team file that has no batting line for him.</summary>
    private static readonly PitcherBattingData DefaultPitcherBatting = new()
    { Ab = 150, H = 22, D2 = 3, D3 = 0, Hr = 1, Bb = 10, So = 55, Hbp = 1 };

    /// <summary>
    /// A 1–6 bunting rating from sacrifice hits per plate appearance. About 1% is an ordinary hitter who bunts now and
    /// then (3); 3% or more is a specialist (6); none at all in a full season is a hitter who never did (1).
    /// </summary>
    public static int BuntRating(int sacrifices, int plateAppearances)
    {
        if (plateAppearances < 50) return 3;   // too few to say
        double rate = (double)sacrifices / plateAppearances;
        return rate switch
        {
            >= 0.030 => 6,
            >= 0.020 => 5,
            >= 0.012 => 4,
            >= 0.006 => 3,
            >= 0.002 => 2,
            _ => 1,
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
        public LeaguePitchingData? LeaguePitching { get; set; }
        public double? LeagueStealRate { get; set; }   // the league's steal success that season; the generator writes it
        public List<BatterData> Batters { get; set; } = [];
        public List<PitcherData> Pitchers { get; set; } = [];
        public TendenciesData? Tendencies { get; set; }
    }

    private sealed class LeaguePitchingData
    {
        public double Era { get; set; }
        public double NonHomeRunHitsPerNine { get; set; }
        public double HomeRunsPerNine { get; set; }
        public double StrikeoutsPerNine { get; set; }
        public double WalksPerNine { get; set; }
    }

    private sealed class TendenciesData
    {
        public double StealAttemptsPerGame { get; set; }
        public double SacrificesPerGame { get; set; }
        public double CompleteGameRate { get; set; }
        public double SavesPerGame { get; set; }
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
        public int? Sh { get; set; }   // sacrifice hits; absent in files generated before it was added
        public Dictionary<string, int[]>? Field { get; set; }
    }

    private sealed class PitcherBattingData
    {
        public int Ab { get; set; }
        public int H { get; set; }
        public int D2 { get; set; }
        public int D3 { get; set; }
        public int Hr { get; set; }
        public int Bb { get; set; }
        public int So { get; set; }
        public int Hbp { get; set; }
        public int? Sh { get; set; }
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
        public int? HoldRunners { get; set; }   // 1–5; no source in the Lahman data, so only hand-entered files have it
        public BatHand? Bats { get; set; }
        public PitcherBattingData? Bat { get; set; }   // his batting line; the generator writes it
        public int TiringFactor { get; set; } = 5;
        public int? Gs { get; set; }   // games started; the generator writes it, and a hand-written file may leave it out
        public PitcherRole Role { get; set; } = PitcherRole.Starter;
        public int Wins { get; set; } = 15;
        public int Losses { get; set; } = 8;
        public int Saves { get; set; }
        public int Cg { get; set; }
        public int Hr { get; set; }
    }
}
