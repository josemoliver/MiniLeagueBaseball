using MiniLeague.SurveyTool;

try
{
    string? output = null;
    int games = 10, seed = 1;
    bool hints = false;
    SurveyDhRule rule = SurveyDhRule.Both;
    for (int i = 0; i < args.Length; i++)
    {
        string value;
        switch (args[i])
        {
            case "--out": output = Next(); break;
            case "--games" when int.TryParse(value = Next(), out int count): games = count; break;
            case "--seed" when int.TryParse(value = Next(), out int parsedSeed): seed = parsedSeed; break;
            case "--dh":
                rule = Next().ToLowerInvariant() switch
                {
                    "designated" => SurveyDhRule.Designated,
                    "pitchers" => SurveyDhRule.Pitchers,
                    "historical" => SurveyDhRule.Historical,
                    "both" => SurveyDhRule.Both,
                    _ => throw new ArgumentException("--dh must be designated, pitchers, historical, or both."),
                };
                break;
            case "--hints": hints = true; break;
            default: throw new ArgumentException($"Unknown or invalid option: {args[i]}");
        }

        string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("An option is missing its value.");
    }

    if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--out <folder> is required.");
    var result = SurveyRunner.Run(new SurveyOptions(games, seed, rule, Hints: hints));
    SurveyReport.Write(result, output);
    Console.WriteLine($"Surveyed {result.Games.Count} games and {result.Rows.Count / 2} turns. Reports: {Path.GetFullPath(output)}");
    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Usage: --out <folder> [--games N] [--seed S] [--dh designated|pitchers|historical|both] [--hints]");
    return 2;
}
