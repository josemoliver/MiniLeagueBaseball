// Computer-manager benchmark: the command line. The work is in ManagerBenchmark.cs.
//
//   dotnet run -c Release --project tools/MiniLeague.ManagerBench -- [--games N] [--seed S] [--check] [--summary FILE]
//
// --games N        games per pairing, team match-up and seat (default 20: 10 pairings x 15 x 2 x N games)
// --seed S         base seed (default 1)
// --dh RULE        the designated hitter rule: designated (the default), historical or pitchers
// --calibrate     compare simulated teams with their real seasons (--games per matchup, default 10; --persona, default analyst)
// --check          check the results against expected-ranges.json and the profile ratings; exit 1 on any mismatch
//                  (the ranges are for the designated-hitter rule, so --check cannot be combined with another --dh)
// --summary FILE   also write the report (and any failures) to FILE as Markdown, e.g. $GITHUB_STEP_SUMMARY

using MiniLeague.ManagerBench;

int IntArg(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
}

string? StringArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var rule = StringArg("--dh")?.ToLowerInvariant() switch
{
    null or "designated" => MiniLeague.Engine.Models.DesignatedHitterSetting.DesignatedHitter,
    "historical" => MiniLeague.Engine.Models.DesignatedHitterSetting.Historical,
    "pitchers" => MiniLeague.Engine.Models.DesignatedHitterSetting.PitchersBat,
    var other => throw new ArgumentException($"Unknown --dh '{other}': use designated, historical or pitchers."),
};
if (args.Contains("--check") && rule != MiniLeague.Engine.Models.DesignatedHitterSetting.DesignatedHitter)
{
    Console.Error.WriteLine("--check compares with ranges measured under the designated-hitter rule; leave out --dh to use it.");
    return 2;
}

// --calibrate compares simulated teams with the real seasons (lahman-reference.json); it reports, it does not check.
if (args.Contains("--calibrate"))
{
    var tallies = MiniLeague.ManagerBench.Calibration.Run(new MiniLeague.ManagerBench.CalibrationOptions(
        IntArg("--games", 10), IntArg("--seed", 1), StringArg("--persona") ?? "analyst",
        StringArg("--dh") is null ? MiniLeague.Engine.Models.DesignatedHitterSetting.Historical : rule));
    var rows = MiniLeague.ManagerBench.Calibration.Compare(tallies,
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lahman-reference.json")));
    int teamGames = tallies.Values.Sum(t => t.Games);
    Console.WriteLine(MiniLeague.ManagerBench.Calibration.Report(rows, teamGames,
        (double)tallies.Values.Sum(t => t.StarterPitches) / teamGames, (double)tallies.Values.Sum(t => t.StarterOuts) / teamGames,
        tallies.Values.Sum(t => t.PitcherPlateAppearances), tallies.Values.Sum(t => t.PitcherSacrifices)));
    var dp = MiniLeague.ManagerBench.Calibration.DoublePlayCounts;
    Console.WriteLine($"double plays: {dp.DoublePlays} in {dp.Chances} chances ({(double)dp.DoublePlays / Math.Max(dp.Chances, 1):P1}); ground outs on them {dp.GroundOuts}, so {(double)dp.DoublePlays / Math.Max(dp.GroundOuts + dp.DoublePlays, 1):P1} of ground balls turned two");
    // --raw METRIC also lists that measure's real and simulated per-game values, team by team.
    if (StringArg("--raw") is { } raw)
        foreach (var r in rows.Where(r => r.Metric == raw))
            Console.WriteLine($"{r.Team,-10} real {r.Real,7:F3}  simulated {r.Sim,7:F3}  ratio {r.Ratio,5:F2}");
    return 0;
}

var result = ManagerBenchmark.Run(new BenchmarkOptions(IntArg("--games", 20), IntArg("--seed", 1), DesignatedHitter: rule));
string report = ManagerBenchmark.Report(result);
Console.WriteLine($"{result.Games} games in {result.Seconds:F0}s");
Console.WriteLine(report);

var failures = new List<string>();
if (args.Contains("--check"))
{
    string ranges = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "expected-ranges.json"));
    failures.AddRange(ManagerBenchmark.CheckRanges(result, ranges));
    failures.AddRange(ManagerBenchmark.CheckProfiles(result, MiniLeague.Engine.AI.ManagerCatalog.Default));
    Console.WriteLine();
    Console.WriteLine(failures.Count == 0 ? "CHECK PASSED" : $"CHECK FAILED ({failures.Count})");
    foreach (var f in failures) Console.WriteLine("  " + f);
}

if (StringArg("--summary") is { } summary)
{
    var lines = new List<string> { "## Computer manager benchmark", "", "```", report.TrimEnd(), "```" };
    if (args.Contains("--check"))
    {
        lines.Add("");
        lines.Add(failures.Count == 0 ? "**Check passed.**" : $"**Check failed ({failures.Count}):**");
        lines.AddRange(failures.Select(f => "- " + f));
    }
    File.AppendAllLines(summary, lines);
}

return failures.Count == 0 ? 0 : 1;
