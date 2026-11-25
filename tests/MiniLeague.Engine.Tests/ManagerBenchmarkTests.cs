using MiniLeague.Engine.AI;
using MiniLeague.ManagerBench;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// Smoke tests for the benchmark tool (tools/MiniLeague.ManagerBench). They run a very small benchmark, so they check
/// that the tool works (it plays, counts, repeats exactly and checks ranges and profiles correctly), not the persona
/// results, which need thousands of games and are checked by running the tool itself with --check (CI does).
/// </summary>
public class ManagerBenchmarkTests
{
    private static readonly string[] TwoPersonas = ["analyst", "smallball"];

    // One game per team match-up and seat: 15 teams x 2 seats = 30 games, a couple of seconds.
    private static BenchmarkResult Small(int seed = 1, IReadOnlyList<string>? personas = null) =>
        ManagerBenchmark.Run(new BenchmarkOptions(GamesPerPairing: 1, Seed: seed, PersonaIds: personas ?? TwoPersonas));

    private static readonly Lazy<BenchmarkResult> Shared = new(() => Small());

    // ── Playing and counting ─────────────────────────────────────────────────

    [Fact]
    public void ItPlaysEveryPairingOverEveryTeamInBothSeats()
    {
        var r = Shared.Value;

        Assert.Equal(30, r.Games);   // one pairing x 15 teams x 2 seats x 1 game
        foreach (var id in TwoPersonas)
            Assert.Equal(30, r.Stats[id].Games);   // each persona is in every game
        // Every game has exactly one winner (the engine plays extra innings), so the wins add up to the games.
        Assert.Equal(30, r.Stats["analyst"].Wins + r.Stats["smallball"].Wins);
    }

    [Fact]
    public void TheCountsAreSane()
    {
        foreach (var (id, t) in Shared.Value.Stats)
        {
            Assert.InRange(t.WinRate, 0, 1);
            Assert.True(t.RunsFor > 0 && t.RunsAgainst > 0, id);
            Assert.True(t.Steals <= t.StealChances && t.Bunts <= t.BuntChances && t.Aggressive <= t.RunnerChances, id);
            Assert.True(t.Walks <= t.WalkChances, id);
            Assert.True(t.CompleteGames <= t.Games, id);
            Assert.True(t.PitchingChanges >= 0, id);
        }
        // The two personas' runs are each other's runs allowed.
        var a = Shared.Value.Stats["analyst"];
        var s = Shared.Value.Stats["smallball"];
        Assert.Equal(a.RunsFor, s.RunsAgainst);
        Assert.Equal(s.RunsFor, a.RunsAgainst);
    }

    [Fact]
    public void ARunWithTheSameOptionsGivesTheSameNumbers_AndADifferentSeedDoesNot()
    {
        string first = ManagerBenchmark.Report(Small());
        string again = ManagerBenchmark.Report(Small());
        string other = ManagerBenchmark.Report(Small(seed: 2));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void TheReportListsEveryPersonaAndExplainsItsColumns()
    {
        string report = ManagerBenchmark.Report(Shared.Value);

        Assert.Contains("30 games", report);
        foreach (var id in TwoPersonas) Assert.Contains(id, report);
        Assert.Contains("win %", report);
        Assert.Contains("def sub", report);
        Assert.Contains("steal%", report);
    }

    // ── The numbers it reports ───────────────────────────────────────────────

    [Fact]
    public void TheWilsonIntervalIsRightForKnownCases()
    {
        var (lo, hi) = ManagerBenchmark.Wilson(50, 100);
        Assert.InRange(lo, 0.403, 0.405);
        Assert.InRange(hi, 0.595, 0.597);

        Assert.Equal((0.0, 0.0), ManagerBenchmark.Wilson(0, 0));
        var (lo0, hi0) = ManagerBenchmark.Wilson(0, 20);
        Assert.Equal(0.0, lo0, 6);
        Assert.InRange(hi0, 0.15, 0.17);
    }

    [Fact]
    public void EachMetricIsComputedFromTheTallyItNames()
    {
        var t = new Tally
        {
            Games = 10, Wins = 6, RunsFor = 40, StealChances = 20, Steals = 4, HitAndRuns = 5, BuntChances = 10, Bunts = 2,
            RunnerChances = 50, Aggressive = 10, WalkChances = 8, Walks = 2, PinchHitters = 3, PinchRunners = 1,
            PitchingChanges = 12, CompleteGames = 2, DefensiveSubs = 5, Pitchouts = 1,
        };

        Assert.Equal(0.6, ManagerBenchmark.Metric(t, "winRate"), 6);
        Assert.Equal(4.0, ManagerBenchmark.Metric(t, "runsPerGame"), 6);
        Assert.Equal(0.2, ManagerBenchmark.Metric(t, "stealRate"), 6);
        Assert.Equal(0.25, ManagerBenchmark.Metric(t, "hitAndRunRate"), 6);
        Assert.Equal(0.2, ManagerBenchmark.Metric(t, "buntRate"), 6);
        Assert.Equal(0.2, ManagerBenchmark.Metric(t, "aggressiveRate"), 6);
        Assert.Equal(0.25, ManagerBenchmark.Metric(t, "walkRate"), 6);
        Assert.Equal(1.4, ManagerBenchmark.Metric(t, "aggressionPerGame"), 6);      // aggressive calls plus steals
        Assert.Equal(1.1, ManagerBenchmark.Metric(t, "smallBallPerGame"), 6);       // bunts, hit and runs and steals
        Assert.Equal(0.4, ManagerBenchmark.Metric(t, "benchPerGame"), 6);
        Assert.Equal(1.2, ManagerBenchmark.Metric(t, "pitchingChangesPerGame"), 6);
        Assert.Equal(0.5, ManagerBenchmark.Metric(t, "defensiveSubsPerGame"), 6);
        Assert.Equal(0.2, ManagerBenchmark.Metric(t, "completeGameRate"), 6);
        Assert.Equal(0.0, ManagerBenchmark.Metric(new Tally(), "stealRate"));       // no chances: zero, not a division error
        Assert.Throws<ArgumentException>(() => ManagerBenchmark.Metric(t, "nonsense"));
    }

    // ── The checks ───────────────────────────────────────────────────────────

    [Fact]
    public void ARangeCheckPassesInsideTheRangeAndNamesWhatIsOutside()
    {
        var r = Shared.Value;
        double win = r.Stats["analyst"].WinRate;

        string inside = $$"""{ "analyst": { "winRate": [{{win - 0.01:0.####}}, {{win + 0.01:0.####}}] } }""";
        string outside = $$"""{ "analyst": { "winRate": [{{win + 0.1:0.####}}, 1.0] }, "smallball": { "runsPerGame": [0, 100] } }""";

        Assert.Empty(ManagerBenchmark.CheckRanges(r, inside));
        var failures = ManagerBenchmark.CheckRanges(r, outside);
        Assert.Single(failures);
        Assert.StartsWith("analyst winRate", failures[0]);
    }

    [Fact]
    public void ARangeCheckIgnoresAPersonaThatWasNotMeasured()
    {
        string json = """{ "slugger": { "winRate": [0.99, 1.0] } }""";
        Assert.Empty(ManagerBenchmark.CheckRanges(Shared.Value, json));
    }

    [Fact]
    public void ARangeCheckRefusesAMetricItDoesNotKnow()
    {
        string json = """{ "analyst": { "nonsense": [0, 1] } }""";
        Assert.Throws<ArgumentException>(() => ManagerBenchmark.CheckRanges(Shared.Value, json));
    }

    private static ManagerPersona Persona(string id, int smallBall) => new(id, id, id, "s", "t",
        "A persona made up for a test of the benchmark's profile check.",
        [new ManagerTrait("Small ball", smallBall), new ManagerTrait("Bench use", 3), new ManagerTrait("Intentional walks", 3)],
        rng => new ComputerManager(rng));

    private static BenchmarkResult Fake(int highBunts, int lowBunts)
    {
        var stats = new Dictionary<string, Tally>
        {
            ["high"] = new Tally { Games = 10, Bunts = highBunts },
            ["low"] = new Tally { Games = 10, Bunts = lowBunts },
        };
        return new BenchmarkResult(new BenchmarkOptions(), ["high", "low"], stats, 20, 0);
    }

    [Fact]
    public void AProfileCheckFailsWhenTheHigherRatedPersonaDoesNoMoreOfTheTrait()
    {
        var catalog = new ManagerCatalog([Persona("high", smallBall: 5), Persona("low", smallBall: 2)]);

        Assert.Empty(ManagerBenchmark.CheckProfiles(Fake(highBunts: 8, lowBunts: 2), catalog));

        var failures = ManagerBenchmark.CheckProfiles(Fake(highBunts: 2, lowBunts: 8), catalog);
        Assert.Contains(failures, f => f.Contains("high") && f.Contains("Small ball"));
    }

    [Fact]
    public void AProfileCheckLeavesAlonePersonasWhoseRatingsAreOnlyOneApart()
    {
        var catalog = new ManagerCatalog([Persona("high", smallBall: 4), Persona("low", smallBall: 3)]);

        Assert.Empty(ManagerBenchmark.CheckProfiles(Fake(highBunts: 0, lowBunts: 9), catalog));
    }

    [Fact]
    public void TheShippedRangesFileIsValid_NamesOnlyRealPersonasAndKnownMetrics()
    {
        string path = Path.Combine(ManagerBenchmark.FindRepoRoot(), "tools", "MiniLeague.ManagerBench", "expected-ranges.json");
        string json = File.ReadAllText(path);

        // A zero-game result: every figure is 0, so this only runs the parse and the metric lookup for every entry.
        var empty = new BenchmarkResult(new BenchmarkOptions(), ManagerCatalog.Default.All.Select(p => p.Id).ToList(),
            ManagerCatalog.Default.All.ToDictionary(p => p.Id, _ => new Tally()), 0, 0);
        _ = ManagerBenchmark.CheckRanges(empty, json);   // throws for an unknown metric

        var ranges = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double[]>>>(json)!;
        Assert.All(ranges.Keys, id => Assert.NotNull(ManagerCatalog.Default.Find(id)));
        Assert.All(ranges.Values.SelectMany(m => m.Values), range =>
        {
            Assert.Equal(2, range.Length);
            Assert.True(range[0] <= range[1]);
        });
    }
}
