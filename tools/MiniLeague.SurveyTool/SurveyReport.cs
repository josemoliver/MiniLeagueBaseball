using System.Globalization;
using System.Reflection;
using System.Text;

namespace MiniLeague.SurveyTool;

public static class SurveyReport
{
    private static readonly PropertyInfo[] Columns = typeof(SurveyRow).GetProperties();

    public static void Write(SurveyResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var encoding = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(outputDirectory, "rows.csv"), RowsCsv(result), encoding);
        File.WriteAllText(Path.Combine(outputDirectory, "groups.csv"), GroupsCsv(result), encoding);
        File.WriteAllText(Path.Combine(outputDirectory, "summary.md"), Summary(result), encoding);
    }

    public static string RowsCsv(SurveyResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns.Select(p => p.Name)));
        foreach (var row in result.Rows)
            sb.AppendLine(string.Join(',', Columns.Select(p => Csv(Convert.ToString(p.GetValue(row), CultureInfo.InvariantCulture) ?? ""))));
        return sb.ToString();
    }

    public static string GroupsCsv(SurveyResult result)
    {
        var sb = new StringBuilder("Side,Call,Bases,Outs,LeveragePhrase,ContactHitter,BatterPower,PitcherCondition,Count,Share,ExampleSeed,ExampleTurn\n");
        foreach (var group in Groups(result))
        {
            var first = group.First();
            string[] values = [group.Key.Side, group.Key.Call, group.Key.Bases,
                group.Key.Outs.ToString(CultureInfo.InvariantCulture), group.Key.Leverage,
                group.Key.Contact, group.Key.Power, group.Key.Condition,
                group.Count().ToString(CultureInfo.InvariantCulture),
                Share(group.Count(), result.Rows.Count / 2).ToString("0.######", CultureInfo.InvariantCulture),
                first.Seed.ToString(CultureInfo.InvariantCulture), first.Turn.ToString(CultureInfo.InvariantCulture)];
            sb.AppendLine(string.Join(',', values.Select(Csv)));
        }
        return sb.ToString();
    }

    public static string Summary(SurveyResult result)
    {
        int turns = result.Rows.Count / 2;
        var sb = new StringBuilder();
        sb.AppendLine("# Play hint situation survey");
        sb.AppendLine();
        sb.AppendLine($"Games: {result.Games.Count}; turns: {turns}; seed: {result.Options.Seed}; DH: {result.Options.Dh}.");
        sb.AppendLine();
        sb.AppendLine("## Calls by side");
        sb.AppendLine();
        sb.AppendLine("| Side | Call | Turns | Share of side's turns |");
        sb.AppendLine("| --- | --- | ---: | ---: |");
        foreach (var group in result.Rows.GroupBy(r => (r.Side, r.Call))
                     .OrderBy(g => g.Key.Side, StringComparer.Ordinal).ThenByDescending(g => g.Count()).ThenBy(g => g.Key.Call, StringComparer.Ordinal))
            sb.AppendLine($"| {group.Key.Side} | {group.Key.Call} | {group.Count()} | {Share(group.Count(), turns):P1} |");
        sb.AppendLine();
        sb.AppendLine("## Fielding choices");
        sb.AppendLine();
        sb.AppendLine("| Choice | Turns | Share of fielding turns |");
        sb.AppendLine("| --- | ---: | ---: |");
        foreach (var pitch in Enum.GetNames<MiniLeague.Engine.Models.PitchType>())
        {
            int count = result.Rows.Count(r => r.Side == "Fielding" && !r.IntentionalWalk && r.Pitch == pitch);
            sb.AppendLine($"| Pitch: {pitch} | {count} | {Share(count, turns):P1} |");
        }
        foreach (var alignment in Enum.GetNames<MiniLeague.Engine.Models.DefensiveAlignment>())
        {
            int count = result.Rows.Count(r => r.Side == "Fielding" && !r.IntentionalWalk && r.Alignment == alignment);
            sb.AppendLine($"| Alignment: {alignment} | {count} | {Share(count, turns):P1} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Largest situation groups");
        sb.AppendLine();
        sb.AppendLine("| Side | Call | Situation | Turns | Share of side's turns | Example seed / turn |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | --- |");
        int covered = 0;
        foreach (var group in Groups(result).Take(15))
        {
            covered += group.Count();
            var k = group.Key;
            string traits = k.Side == "Batting" ? $"contact {k.Contact}, power {k.Power}" : $"pitcher {k.Condition}, power {k.Power}";
            var first = group.First();
            sb.AppendLine($"| {k.Side} | {k.Call} | {k.Bases}, {k.Outs} out, {k.Leverage}, {traits} | {group.Count()} | {Share(group.Count(), turns):P1} | {first.Seed} / {first.Turn} |");
        }
        sb.AppendLine();
        sb.AppendLine($"Largest fifteen groups cover {Share(covered, result.Rows.Count):P1} of all side-turn rows.");
        sb.AppendLine();
        sb.AppendLine("## Leverage phrases");
        sb.AppendLine();
        foreach (var group in result.Rows.GroupBy(r => r.LeveragePhrase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"- {group.Key}: {group.Count()} side-turns ({Share(group.Count(), result.Rows.Count):P1})");
        sb.AppendLine();
        sb.AppendLine("## Rare or absent calls");
        sb.AppendLine();
        foreach (var (side, call) in ExpectedCalls)
        {
            int count = result.Rows.Count(r => r.Side == side && r.Call == call);
            if (count == 0 || Share(count, turns) < 0.01)
                sb.AppendLine($"- {side} / {call}: {count} turns ({Share(count, turns):P1})");
        }
        if (result.Options.Hints)
        {
            sb.AppendLine();
            sb.AppendLine("## Hint wording audit");
            sb.AppendLine();
            int withConditional = result.Rows.Count(r => r.HintHasConditional);
            int generic = result.Rows.Count(r => r.HintReasonId is "swing-open" or "normal-open");
            int digits = result.Rows.Count(r => r.HintText.Any(char.IsDigit));
            int mismatches = result.Rows.Count(r => r.HintDiffers);
            var repeated = result.Rows.GroupBy(r => (r.Seed, r.Side, r.HintReason))
                .Where(g => g.Key.HintReason.Length > 0 && g.Count() > 1).ToList();
            int repeatRows = repeated.Sum(g => g.Count() - 1);
            sb.AppendLine($"- Turns with a conditional line: {withConditional} ({Share(withConditional, result.Rows.Count):P1}).");
            sb.AppendLine($"- Turns with a generic reason: {generic} ({Share(generic, result.Rows.Count):P1}).");
            sb.AppendLine($"- Repeated reason sentences within one side's game: {repeatRows} extra uses.");
            sb.AppendLine($"- Hint text containing a digit: {digits} turns.");
            sb.AppendLine($"- Hint call differs from manager: {mismatches} side-turns.");
            sb.AppendLine();
            sb.AppendLine("### Most repeated reasons");
            sb.AppendLine();
            foreach (var group in result.Rows.Where(r => r.HintReason.Length > 0).GroupBy(r => r.HintReason)
                         .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(12))
                sb.AppendLine($"- {group.Count()} uses: {group.Key}");
            sb.AppendLine();
            sb.AppendLine("### First differing calls");
            sb.AppendLine();
            foreach (var row in result.Rows.Where(r => r.HintDiffers).Take(20))
                sb.AppendLine($"- Seed {row.Seed}, turn {row.Turn}, {row.Side}: hint {row.HintCall}; manager {row.Call}.");
        }
        return sb.ToString();
    }

    private static readonly (string Side, string Call)[] ExpectedCalls =
    [
        ("Batting", "SwingAway"), ("Batting", "SwingAgressive"), ("Batting", "Steal"),
        ("Batting", "HitAndRun"), ("Batting", "SacrificeBunt"), ("Batting", "Squeeze"),
        ("Batting", "SafetySqueeze"), ("Fielding", "IntentionalWalk"),
    ];

    private static IOrderedEnumerable<IGrouping<(string Side, string Call, string Bases, int Outs, string Leverage,
        string Contact, string Power, string Condition), SurveyRow>> Groups(SurveyResult result) =>
        result.Rows.GroupBy(r => (r.Side, r.Call, r.Bases, r.Outs, r.LeveragePhrase,
            r.Side == "Batting" ? r.ContactHitter.ToString() : "",
            r.BatterPower.ToString(CultureInfo.InvariantCulture),
            r.Side == "Fielding" ? r.PitcherCondition : ""))
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Side, StringComparer.Ordinal)
        .ThenBy(g => g.Key.Call, StringComparer.Ordinal).ThenBy(g => g.Key.Bases, StringComparer.Ordinal);

    private static double Share(int count, int total) => total == 0 ? 0 : (double)count / total;

    private static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value : '"' + value.Replace("\"", "\"\"") + '"';
}
