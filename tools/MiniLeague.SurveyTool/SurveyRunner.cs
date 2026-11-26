using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.SurveyTool;

public enum SurveyDhRule { Designated, Pitchers, Historical, Both }

public sealed record SurveyOptions(int GamesPerMatchup = 10, int Seed = 1, SurveyDhRule Dh = SurveyDhRule.Both,
    string? TeamsDirectory = null, int? MatchupLimit = null, bool Hints = false);

public sealed record SurveyGame(int Seed, string Visitor, string Home, DesignatedHitterSetting Rule,
    int VisitorRuns, int HomeRuns, int Turns);

public sealed record SurveyRow
{
    public int Seed { get; init; }
    public string DhRule { get; init; } = "";
    public string Visitor { get; init; } = "";
    public string Home { get; init; } = "";
    public int Turn { get; init; }
    public string Side { get; init; } = "";
    public int Inning { get; init; }
    public string Half { get; init; } = "";
    public int Outs { get; init; }
    public string Bases { get; init; } = "";
    public int ScoreMargin { get; init; }
    public double Leverage { get; init; }
    public string LeveragePhrase { get; init; } = "";
    public string BatterHand { get; init; } = "";
    public int BatterPower { get; init; }
    public int BatterSpeed { get; init; }
    public int BatterBunting { get; init; }
    public int BatterSacrifice { get; init; }
    public bool ContactHitter { get; init; }
    public bool PitcherBatting { get; init; }
    public bool OnDeckWeaker { get; init; }
    public string PitcherHand { get; init; } = "";
    public string PitcherCondition { get; init; } = "";
    public bool NotYetLoose { get; init; }
    public double GroundBallRate { get; init; }
    public string ShowingAlignment { get; init; } = "";
    public string Call { get; init; } = "";
    public string StealPlan { get; init; } = "";
    public string Pitch { get; init; } = "";
    public string Alignment { get; init; } = "";
    public bool IntentionalWalk { get; init; }
    public bool PinchHitter { get; init; }
    public bool PinchRunner { get; init; }
    public string HintCall { get; init; } = "";
    public string HintReasonId { get; init; } = "";
    public string HintReason { get; init; } = "";
    public string HintText { get; init; } = "";
    public bool HintHasConditional { get; init; }
    public bool HintDiffers { get; init; }
}

public sealed record SurveyResult(SurveyOptions Options, IReadOnlyList<SurveyGame> Games, IReadOnlyList<SurveyRow> Rows);

public static class SurveyRunner
{
    public static SurveyResult Run(SurveyOptions options)
    {
        if (options.GamesPerMatchup is < 1 or > 1000) throw new ArgumentException("--games must be between 1 and 1000.");
        if (options.MatchupLimit is < 1) throw new ArgumentException("MatchupLimit must be positive.");
        string teamsDir = options.TeamsDirectory ?? Path.Combine(FindRepoRoot(), "src", "MiniLeague.Web", "wwwroot", "data", "teams");
        var files = Directory.GetFiles(teamsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length < 2) throw new ArgumentException("The survey needs at least two team files.");
        var json = files.Select(File.ReadAllText).ToArray();
        var rules = options.Dh == SurveyDhRule.Both
            ? new[] { DesignatedHitterSetting.DesignatedHitter, DesignatedHitterSetting.PitchersBat }
            : [options.Dh switch
            {
                SurveyDhRule.Designated => DesignatedHitterSetting.DesignatedHitter,
                SurveyDhRule.Pitchers => DesignatedHitterSetting.PitchersBat,
                _ => DesignatedHitterSetting.Historical,
            }];
        var games = new List<SurveyGame>();
        var rows = new List<SurveyRow>();
        int matchups = 0;
        for (int visitor = 0; visitor < files.Length; visitor++)
        for (int home = 0; home < files.Length; home++)
        {
            if (visitor == home) continue;
            if (options.MatchupLimit is int limit && matchups >= limit) return new(options, games, rows);
            matchups++;
            for (int ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
            for (int gameNumber = 0; gameNumber < options.GamesPerMatchup; gameNumber++)
            {
                int seed = SeedFor(options.Seed, visitor, home, (int)rules[ruleIndex], gameNumber, files.Length);
                PlayOne(json[visitor], json[home], rules[ruleIndex], seed, options.Hints, games, rows);
            }
        }
        return new(options, games, rows);
    }

    // WHY: fixed arithmetic makes a reported seed reproducible across processes and keeps each normal survey slot distinct.
    public static int SeedFor(int baseSeed, int visitor, int home, int ruleIndex, int gameNumber, int teamCount) =>
        (int)(((((long)visitor * teamCount + home) * 3_003 + ruleIndex * 1_001 + gameNumber + baseSeed)
            % (int.MaxValue - 2) + (int.MaxValue - 2)) % (int.MaxValue - 2));

    private static void PlayOne(string visitorJson, string homeJson, DesignatedHitterSetting rule, int seed, bool includeHints,
        List<SurveyGame> games, List<SurveyRow> rows)
    {
        var visitor = TeamLoader.Parse(visitorJson);
        var home = TeamLoader.Parse(homeJson);
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)),
            new Random(seed), ManagerCatalog.Default, managerSeed: seed);
        game.StartGame(visitor, home, ManagerControl.Computer, ManagerControl.Computer,
            options: GameOptions.None with { DesignatedHitter = rule }, visitorManagerId: "analyst", homeManagerId: "analyst");
        var state = game.State!;
        int turn = 0;
        while (state.Status == GameStatus.InProgress)
        {
            if (++turn > 4000) throw new InvalidOperationException($"Game with seed {seed} exceeded the turn limit.");
            // WHY: collect before the turn because a steal or a substitution can change the bases, batter and pitcher.
            var batting = Capture(state, seed, rule, visitor.Abbreviation, home.Abbreviation, turn, "Batting");
            var fielding = Capture(state, seed, rule, visitor.Abbreviation, home.Abbreviation, turn, "Fielding");
            var battingTeam = state.OffenseTeam;
            var pitcherPlayer = state.CurrentPitcher.Player;
            var lineup = state.LineupFor(state.IsTopOfInning);
            var onDeck = battingTeam.Roster.First(p => p.Id == lineup[(state.CurrentBatterIndex + 1) % lineup.Count]);
            var stealPlan = RunningRules.StealPlanFor(state);
            var battingHint = includeHints ? PlayHintAdvisor.Get(state, PlayHintSide.Offense) : null;
            var fieldingHint = includeHints ? PlayHintAdvisor.Get(state, PlayHintSide.Defense) : null;
            if (game.ExecuteComputerTurn() is null) throw new InvalidOperationException($"Game with seed {seed} stopped before final.");
            var offense = game.LastOffense!;
            var defense = game.LastDefense!;
            if (offense.PinchHitterPlayerId is int pinchId)
            {
                var incoming = battingTeam.Roster.First(p => p.Id == pinchId);
                batting = WithBatter(batting, incoming, onDeck, pitcherPlayer);
                fielding = WithBatter(fielding, incoming, onDeck, pitcherPlayer);
            }
            rows.Add(batting with
            {
                Call = offense.Strategy.ToString(),
                StealPlan = offense.Strategy == OffensiveStrategy.Steal ? stealPlan.ToString() : "",
                PinchHitter = offense.PinchHitterPlayerId is not null,
                PinchRunner = offense.PinchRunnerPlayerId is not null,
                HintCall = battingHint?.Strategy.ToString() ?? "",
                HintReasonId = battingHint?.ReasonId ?? "",
                HintReason = battingHint?.Reason ?? "",
                HintText = battingHint is null ? "" : HintText(battingHint),
                HintHasConditional = battingHint?.Conditional is not null,
                HintDiffers = battingHint is not null && battingHint.Strategy != offense.Strategy,
            });
            rows.Add(fielding with
            {
                Call = defense.IntentionalWalk ? "IntentionalWalk" : $"{defense.PitchType}/{defense.Alignment}",
                Pitch = defense.PitchType.ToString(),
                Alignment = defense.Alignment.ToString(),
                IntentionalWalk = defense.IntentionalWalk,
                PinchHitter = offense.PinchHitterPlayerId is not null,
                PinchRunner = offense.PinchRunnerPlayerId is not null,
                HintCall = fieldingHint is null ? "" : fieldingHint.IntentionalWalk ? "IntentionalWalk" : $"{fieldingHint.Pitch}/{fieldingHint.Alignment}",
                HintReasonId = fieldingHint?.ReasonId ?? "",
                HintReason = fieldingHint?.Reason ?? "",
                HintText = fieldingHint is null ? "" : HintText(fieldingHint),
                HintHasConditional = fieldingHint?.Conditional is not null,
                HintDiffers = fieldingHint is not null && (fieldingHint.IntentionalWalk != defense.IntentionalWalk
                    || !fieldingHint.IntentionalWalk && (fieldingHint.Pitch != defense.PitchType || fieldingHint.Alignment != defense.Alignment)),
            });
        }
        games.Add(new(seed, visitor.Abbreviation, home.Abbreviation, rule,
            state.VisitorTotalScore, state.HomeTotalScore, turn));
    }

    private static string HintText(PlayHint hint) => string.Join(" ",
        new[] { hint.Situation, hint.Matchup, hint.PitcherNote, hint.PlatoonNote, hint.LeverageNote,
            hint.Call, hint.Reason, hint.Conditional }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static SurveyRow Capture(GameState state, int seed, DesignatedHitterSetting rule,
        string visitor, string home, int turn, string side)
    {
        var batter = state.CurrentBatter;
        var pitcher = state.CurrentPitcher;
        var lineup = state.LineupFor(state.IsTopOfInning);
        var next = state.OffenseTeam.Roster.First(p => p.Id == lineup[(state.CurrentBatterIndex + 1) % lineup.Count]);
        var batterStats = batter.GetBattingStats(pitcher.Player.ThrowHand);
        var nextStats = next.GetBattingStats(pitcher.Player.ThrowHand);
        int offenseMargin = state.IsTopOfInning
            ? state.VisitorTotalScore - state.HomeTotalScore : state.HomeTotalScore - state.VisitorTotalScore;
        return new SurveyRow
        {
            Seed = seed, DhRule = rule.ToString(), Visitor = visitor, Home = home, Turn = turn, Side = side,
            Inning = state.CurrentInning, Half = state.IsTopOfInning ? "Top" : "Bottom", Outs = state.Outs,
            Bases = $"{(state.RunnerOnFirst is null ? '-' : '1')}{(state.RunnerOnSecond is null ? '-' : '2')}{(state.RunnerOnThird is null ? '-' : '3')}",
            ScoreMargin = side == "Batting" ? offenseMargin : -offenseMargin,
            Leverage = ManagerDecisionModel.Leverage(state),
            LeveragePhrase = LeveragePhrase(state, side),
            BatterHand = batter.BatsAgainst(pitcher.Player.ThrowHand).ToString(),
            BatterPower = batter.PowerRating, BatterSpeed = batter.SpeedRating,
            BatterBunting = batter.BuntingAbility, BatterSacrifice = batter.SacrificeAbility,
            ContactHitter = (batter.Overall?.BattingAverage ?? 0) >= 0.270 && (batter.Overall?.Strikeouts ?? 99) < 80,
            PitcherBatting = state.CurrentBatterPosition == Position.P,
            OnDeckWeaker = nextStats.OnBasePercentage + nextStats.SluggingPct < batterStats.OnBasePercentage + batterStats.SluggingPct,
            PitcherHand = pitcher.Player.ThrowHand.ToString(), PitcherCondition = pitcher.Condition.ToString(),
            NotYetLoose = pitcher.BattersFaced - pitcher.SettleFrom < FatigueModel.SettleInBatters && pitcher.EntryModifier < 1,
            GroundBallRate = pitcher.Player.GetPitchingStats(batter.BatsAgainst(pitcher.Player.ThrowHand)).GroundBallRate,
            ShowingAlignment = (state.IsTopOfInning ? state.HomeAlignment : state.VisitorAlignment).ToString(),
        };
    }

    private static SurveyRow WithBatter(SurveyRow row, Player batter, Player onDeck, Player pitcher)
    {
        var pitcherHand = pitcher.ThrowHand;
        var stats = batter.GetBattingStats(pitcherHand);
        var next = onDeck.GetBattingStats(pitcherHand);
        return row with
        {
            BatterHand = batter.BatsAgainst(pitcherHand).ToString(), BatterPower = batter.PowerRating,
            BatterSpeed = batter.SpeedRating, BatterBunting = batter.BuntingAbility,
            BatterSacrifice = batter.SacrificeAbility,
            ContactHitter = (batter.Overall?.BattingAverage ?? 0) >= 0.270 && (batter.Overall?.Strikeouts ?? 99) < 80,
            PitcherBatting = batter.IsPitcher,
            OnDeckWeaker = next.OnBasePercentage + next.SluggingPct < stats.OnBasePercentage + stats.SluggingPct,
            GroundBallRate = pitcher.GetPitchingStats(batter.BatsAgainst(pitcherHand)).GroundBallRate,
        };
    }

    public static string LeveragePhrase(GameState state, string side)
    {
        if (ManagerDecisionModel.Leverage(state) < 0.3) return "Run barely changes result";
        int margin = state.IsTopOfInning
            ? state.VisitorTotalScore - state.HomeTotalScore : state.HomeTotalScore - state.VisitorTotalScore;
        if (side == "Fielding")
            return state.CurrentInning >= 7 && Math.Abs(margin) <= 2
                ? "Late close-game defense" : "Chance to contain an inning";
        int runners = (state.RunnerOnFirst is null ? 0 : 1) + (state.RunnerOnSecond is null ? 0 : 1)
            + (state.RunnerOnThird is null ? 0 : 1);
        if (state.CurrentInning >= 7 && margin <= 0 && -margin <= runners + 1) return "Late tying or go-ahead chance";
        if (state.CurrentInning >= 7 && margin is 1 or 2) return "Protecting a narrow lead";
        return "Chance to build an inning";
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MiniLeagueBaseball.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Run the survey from inside the repository.");
    }
}
