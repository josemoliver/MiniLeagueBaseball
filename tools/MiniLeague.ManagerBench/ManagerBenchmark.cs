using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.ManagerBench;

/// <summary>What one persona did over all its games.</summary>
public sealed class Tally
{
    public int Games, Wins, RunsFor, RunsAgainst, CompleteGames, PitchingChanges;
    public int StealChances, Steals, HitAndRuns, BuntChances, Bunts, RunnerChances, Aggressive, ThrownOut;
    public int WalkChances, Walks, Pitchouts, PinchHitters, PinchRunners, DefensiveSubs;

    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
    public double PerGame(double count) => Games == 0 ? 0 : count / Games;
}

/// <param name="GamesPerPairing">Games for each pair of personas, team match-up and seat.</param>
/// <param name="Seed">Base seed: the same options give the same numbers on every run.</param>
/// <param name="TeamsDirectory">Where the team JSON files are; the repository's own when null.</param>
/// <param name="Catalog">The personas to measure; the app's catalog when null.</param>
/// <param name="PersonaIds">Only these personas (all in the catalog when null), for a faster run.</param>
/// <param name="DesignatedHitter">The designated hitter rule for every game. expected-ranges.json is for the default.</param>
public sealed record BenchmarkOptions(int GamesPerPairing = 20, int Seed = 1, string? TeamsDirectory = null,
    ManagerCatalog? Catalog = null, IReadOnlyList<string>? PersonaIds = null,
    DesignatedHitterSetting DesignatedHitter = DesignatedHitterSetting.DesignatedHitter);

public sealed record BenchmarkResult(BenchmarkOptions Options, IReadOnlyList<string> Ids,
    IReadOnlyDictionary<string, Tally> Stats, int Games, double Seconds);

/// <summary>
/// Computer-manager benchmark. Plays seeded computer-versus-computer games between every pair of personas, over every
/// team (each against the team seven places along the list), with each persona in both the visitor and home seat, and
/// reports what each did. Every random source is seeded, so the same options give the same numbers on every run.
/// </summary>
public static class ManagerBenchmark
{
    public static BenchmarkResult Run(BenchmarkOptions options)
    {
        var catalog = options.Catalog ?? ManagerCatalog.Default;
        string teamsDir = options.TeamsDirectory
            ?? Path.Combine(FindRepoRoot(), "src", "MiniLeague.Web", "wwwroot", "data", "teams");
        var teamFiles = Directory.GetFiles(teamsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var json = teamFiles.ToDictionary(f => f, File.ReadAllText);
        Team Load(string file) => TeamLoader.Parse(json[file]);

        var ids = (options.PersonaIds ?? catalog.All.Select(p => p.Id).ToList()).ToList();
        var stats = ids.ToDictionary(id => id, _ => new Tally());

        var clock = Stopwatch.StartNew();
        int played = 0;
        for (int a = 0; a < ids.Count; a++)
        for (int b = a + 1; b < ids.Count; b++)
        for (int t = 0; t < teamFiles.Count; t++)
        for (int seat = 0; seat < 2; seat++)
        for (int g = 0; g < options.GamesPerPairing; g++)
        {
            int seed = options.Seed + ((((a * 16 + b) * 32 + t) * 2 + seat) * 1000) + g;
            string visitorId = seat == 0 ? ids[a] : ids[b], homeId = seat == 0 ? ids[b] : ids[a];
            PlayOne(catalog, stats, options.DesignatedHitter, seed, Load(teamFiles[t]), Load(teamFiles[(t + 7) % teamFiles.Count]),
                visitorId, homeId);
            played++;
        }
        clock.Stop();
        return new BenchmarkResult(options, ids, stats, played, clock.Elapsed.TotalSeconds);
    }

    private static void PlayOne(ManagerCatalog catalog, Dictionary<string, Tally> stats, DesignatedHitterSetting rule, int seed,
        Team visitor, Team home, string visitorId, string homeId)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)),
            new Random(seed), catalog, managerSeed: seed);
        game.StartGame(visitor, home, ManagerControl.Computer, ManagerControl.Computer,
            options: GameOptions.None with { DesignatedHitter = rule }, visitorManagerId: visitorId, homeManagerId: homeId);
        var s = game.State!;

        for (int guard = 0; s.Status == GameStatus.InProgress && guard < 4000; guard++)
        {
            bool top = s.IsTopOfInning;
            var batting = stats[top ? visitorId : homeId];
            var fielding = stats[top ? homeId : visitorId];
            bool first = s.RunnerOnFirst is not null, second = s.RunnerOnSecond is not null, third = s.RunnerOnThird is not null;
            bool stealChance = first && !second && s.Outs < 2;
            bool buntChance = (first || second) && !third && s.Outs == 0;
            bool walkChance = !first && (second || third) && s.Outs < 2;

            var result = game.ExecuteComputerTurn();
            if (result is null) break;
            var offense = game.LastOffense!;
            var defense = game.LastDefense!;

            if (stealChance) batting.StealChances++;
            if (buntChance) batting.BuntChances++;
            if (first || second || third) batting.RunnerChances++;
            if (walkChance) fielding.WalkChances++;
            switch (offense.Strategy)
            {
                case OffensiveStrategy.Steal when stealChance: batting.Steals++; break;
                case OffensiveStrategy.HitAndRun when stealChance: batting.HitAndRuns++; break;
                case OffensiveStrategy.SacrificeBunt when buntChance: batting.Bunts++; break;
                case OffensiveStrategy.SwingAgressive when first || second || third: batting.Aggressive++; break;
            }
            if (offense.PinchHitterPlayerId is not null) batting.PinchHitters++;
            if (offense.PinchRunnerPlayerId is not null) batting.PinchRunners++;
            if (result.ThrownOutFrom is not null) batting.ThrownOut++;
            if (defense.IntentionalWalk && walkChance) fielding.Walks++;
            if (defense.PitchType == PitchType.Pitchout) fielding.Pitchouts++;
        }

        foreach (var (id, visitorSide) in new[] { (visitorId, true), (homeId, false) })
        {
            var t = stats[id];
            int runsFor = visitorSide ? s.VisitorTotalScore : s.HomeTotalScore;
            int runsAgainst = visitorSide ? s.HomeTotalScore : s.VisitorTotalScore;
            int pitchers = (visitorSide ? s.Box.Visitor : s.Box.Home).Pitchers.Count;
            t.Games++;
            if (runsFor > runsAgainst) t.Wins++;
            t.RunsFor += runsFor;
            t.RunsAgainst += runsAgainst;
            t.PitchingChanges += pitchers - 1;
            if (pitchers == 1) t.CompleteGames++;
            // A defensive replacement is logged as "<TEAM>: X replaces Y at Z." (pinch hitters, runners and relievers read differently).
            string abbreviation = (visitorSide ? s.VisitorTeam : s.HomeTeam).Abbreviation;
            t.DefensiveSubs += s.PlayByPlay.Count(e => e.Description.StartsWith(abbreviation + ": ") && e.Description.Contains(" replaces "));
        }
    }

    // ── Report ───────────────────────────────────────────────────────────────

    /// <summary>The results as a table. Contains nothing that varies from run to run (no timing), so equal runs give equal text.</summary>
    public static string Report(BenchmarkResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Games} games (games per pairing/team/seat {r.Options.GamesPerPairing}, seed {r.Options.Seed}, " +
                      $"designated hitter: {r.Options.DesignatedHitter})");
        sb.AppendLine();
        sb.AppendLine($"{"manager",-11}{"win %",8}{"95% CI",16}{"runs",7}{"allowed",9}" +
                      $"{"steal%",8}{"bunt%",7}{"H&R%",7}{"aggr%",7}{"thrown",8}{"IBB%",7}{"pitchout",9}{"PH",6}{"PR",6}{"pitch chg",10}{"CG%",6}{"def sub",8}");
        foreach (var id in r.Ids)
        {
            var s = r.Stats[id];
            var (lo, hi) = Wilson(s.Wins, s.Games);
            sb.AppendLine($"{id,-11}{s.WinRate,8:P1}{$"{lo:P1}–{hi:P1}",16}{s.PerGame(s.RunsFor),7:F2}{s.PerGame(s.RunsAgainst),9:F2}" +
                          $"{Pct(s.Steals, s.StealChances),8}{Pct(s.Bunts, s.BuntChances),7}{Pct(s.HitAndRuns, s.StealChances),7}" +
                          $"{Pct(s.Aggressive, s.RunnerChances),7}{s.PerGame(s.ThrownOut),8:F2}{Pct(s.Walks, s.WalkChances),7}" +
                          $"{s.PerGame(s.Pitchouts),9:F2}{s.PerGame(s.PinchHitters),6:F2}{s.PerGame(s.PinchRunners),6:F2}" +
                          $"{s.PerGame(s.PitchingChanges),10:F2}{Pct(s.CompleteGames, s.Games),6}{s.PerGame(s.DefensiveSubs),8:F2}");
        }
        sb.AppendLine();
        sb.AppendLine("steal%, H&R%: share of chances (runner on first, second open, fewer than two out). bunt%: runner on first or");
        sb.AppendLine("second, third open, nobody out. aggr%: plate appearances with a runner on. IBB%: first open, a runner in");
        sb.AppendLine("scoring position, fewer than two out. thrown, pitchout, PH, PR, pitch chg, def sub: per game. CG%: games the starter finished.");
        return sb.ToString();
    }

    // ── Checks ───────────────────────────────────────────────────────────────

    /// <summary>Each measured figure must sit inside the range expected-ranges.json gives for that persona. Returns the failures.</summary>
    public static List<string> CheckRanges(BenchmarkResult r, string rangesJson)
    {
        var failures = new List<string>();
        var ranges = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double[]>>>(rangesJson)!;
        foreach (var (id, metrics) in ranges)
        {
            if (!r.Stats.TryGetValue(id, out var s)) continue;   // a persona that was not measured in this run
            foreach (var (metric, range) in metrics)
            {
                double value = Metric(s, metric);
                if (value < range[0] || value > range[1])
                    failures.Add($"{id} {metric} = {value:0.###}, expected {range[0]}–{range[1]}");
            }
        }
        return failures;
    }

    // WHY: a persona's profile ratings are a promise to the player. Where two personas' ratings for a trait differ by two or
    // more, the one rated higher must measurably do more of it, or the profile is not true.
    public static List<string> CheckProfiles(BenchmarkResult r, ManagerCatalog catalog)
    {
        var failures = new List<string>();
        var traitMetric = new Dictionary<string, string>
        {
            ["Aggression on the bases"] = "aggressionPerGame",
            ["Small ball"] = "smallBallPerGame",
            ["Bench use"] = "benchPerGame",
            ["Quick with the bullpen"] = "pitchingChangesPerGame",
            ["Intentional walks"] = "walksPerGame",
            ["Late defensive subs"] = "defensiveSubsPerGame",
        };
        var measured = catalog.All.Where(p => r.Stats.ContainsKey(p.Id)).ToList();
        foreach (var (trait, metric) in traitMetric)
            foreach (var high in measured)
            foreach (var low in measured)
            {
                int hr = high.Traits.FirstOrDefault(t => t.Label == trait)?.Rating ?? 0;
                int lr = low.Traits.FirstOrDefault(t => t.Label == trait)?.Rating ?? 0;
                if (hr - lr < 2) continue;
                double hv = Metric(r.Stats[high.Id], metric), lv = Metric(r.Stats[low.Id], metric);
                if (hv <= lv)
                    failures.Add($"profile: {high.Id} is rated {hr} for \"{trait}\" and {low.Id} {lr}, but {metric} is {hv:0.###} vs {lv:0.###}");
            }
        return failures;
    }

    /// <summary>One named figure for a persona, as used in expected-ranges.json.</summary>
    public static double Metric(Tally s, string name) => name switch
    {
        "winRate" => s.WinRate,
        "runsPerGame" => s.PerGame(s.RunsFor),
        "stealRate" => Ratio(s.Steals, s.StealChances),
        "buntRate" => Ratio(s.Bunts, s.BuntChances),
        "hitAndRunRate" => Ratio(s.HitAndRuns, s.StealChances),
        "aggressiveRate" => Ratio(s.Aggressive, s.RunnerChances),
        "walkRate" => Ratio(s.Walks, s.WalkChances),
        "aggressionPerGame" => s.PerGame(s.Aggressive + s.Steals),
        "smallBallPerGame" => s.PerGame(s.Bunts + s.HitAndRuns + s.Steals),
        "benchPerGame" => s.PerGame(s.PinchHitters + s.PinchRunners),
        "pitchingChangesPerGame" => s.PerGame(s.PitchingChanges),
        "walksPerGame" => s.PerGame(s.Walks),
        "defensiveSubsPerGame" => s.PerGame(s.DefensiveSubs),
        "pitchoutsPerGame" => s.PerGame(s.Pitchouts),
        "completeGameRate" => Ratio(s.CompleteGames, s.Games),
        _ => throw new ArgumentException($"Unknown metric '{name}' in expected-ranges.json"),
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static double Ratio(double part, double whole) => whole == 0 ? 0 : part / whole;
    private static string Pct(double part, double whole) => whole == 0 ? "-" : $"{part / whole:P1}";

    /// <summary>The 95% Wilson interval for a win rate.</summary>
    public static (double Lo, double Hi) Wilson(int wins, int games)
    {
        if (games == 0) return (0, 0);
        const double z = 1.96;
        double p = (double)wins / games, n = games;
        double centre = (p + z * z / (2 * n)) / (1 + z * z / n);
        double half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n);
        return (centre - half, centre + half);
    }

    public static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MiniLeagueBaseball.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Run the benchmark from inside the repository.");
    }
}
