using System.Text.Json;
using System.Text.Json.Serialization;
using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Services;

public record RunnerSnapshot(int PlayerId, int BattingSlot, int ResponsiblePitcherId);
public record PitcherSnapshot(int PlayerId, int PitchCount, int BattersFaced, int SettleFrom, int RunsAllowed, double EntryModifier, PitcherCondition Condition);
public record BullpenSnapshot(int PlayerId, bool IsWarming, int BattersFacedWarming);

/// <summary>
/// A game in progress as plain data (player IDs, not objects), small enough for browser storage.
/// Teams are referenced by ID and must be loaded again on resume.
/// </summary>
public class GameSnapshot
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Description { get; set; } = "";   // e.g. "NYY 3, BRO 2 — ▼5", for the resume prompt

    public int VisitorTeamId { get; set; }
    public int HomeTeamId { get; set; }
    public string StadiumId { get; set; } = "generic";
    public GameOptions Options { get; set; } = GameOptions.None;
    public ManagerControl VisitorControl { get; set; }
    public ManagerControl HomeControl { get; set; }
    public int VisitorHintsUsed { get; set; }
    public int HomeHintsUsed { get; set; }
    public int VisitorLastHintPlay { get; set; } = -1;
    public int HomeLastHintPlay { get; set; } = -1;
    public string? VisitorManagerId { get; set; }   // null in games saved before there was a choice of manager
    public string? HomeManagerId { get; set; }
    public GameStatus Status { get; set; }

    public List<int> VisitorScoreByInning { get; set; } = [];
    public List<int> HomeScoreByInning { get; set; } = [];
    public int CurrentInning { get; set; }
    public bool IsTopOfInning { get; set; }
    public int Outs { get; set; }

    public RunnerSnapshot? RunnerOnFirst { get; set; }
    public RunnerSnapshot? RunnerOnSecond { get; set; }
    public RunnerSnapshot? RunnerOnThird { get; set; }

    public List<int> VisitorLineup { get; set; } = [];
    public List<int> HomeLineup { get; set; } = [];
    public Dictionary<int, Position> VisitorPositions { get; set; } = [];
    public Dictionary<int, Position> HomePositions { get; set; } = [];
    public int VisitorBatterIndex { get; set; }
    public int HomeBatterIndex { get; set; }

    public PitcherSnapshot VisitorPitcher { get; set; } = null!;
    public PitcherSnapshot HomePitcher { get; set; } = null!;
    public List<BullpenSnapshot> VisitorBullpen { get; set; } = [];
    public List<BullpenSnapshot> HomeBullpen { get; set; } = [];
    public List<int> VisitorUsed { get; set; } = [];
    public List<int> HomeUsed { get; set; } = [];

    public DefensiveAlignment VisitorAlignment { get; set; }
    public DefensiveAlignment HomeAlignment { get; set; }

    public List<PlayEvent> PlayByPlay { get; set; } = [];

    public BoxScore Box { get; set; } = new();
    public bool ErrorThisHalfInning { get; set; }
    public bool EndedByRain { get; set; }
    public int RainDelays { get; set; }
    public bool VisitorManagerEjected { get; set; }
    public bool HomeManagerEjected { get; set; }
    public InjuryNotice? PendingInjury { get; set; }
    public List<RecapPlay> Recap { get; set; } = [];
}

public static class GameSnapshots
{
    /// <summary>Most recent play-by-play entries kept in a save (newest first).</summary>
    public const int MaxLoggedPlays = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(GameSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    /// <summary>Returns null when the text is not a readable snapshot of the current version.</summary>
    public static GameSnapshot? FromJson(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<GameSnapshot>(json, Json);
            return snapshot is { Version: GameSnapshot.CurrentVersion } ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static GameSnapshot Capture(GameState s)
    {
        static RunnerSnapshot? Runner(BaseRunner? r) => r is null ? null : new(r.Player.Id, r.BattingSlot, r.ResponsiblePitcherId);
        static PitcherSnapshot Pitcher(ActivePitcher p) =>
            new(p.Player.Id, p.PitchCount, p.BattersFaced, p.SettleFrom, p.RunsAllowed, p.EntryModifier, p.Condition);
        static List<BullpenSnapshot> Pen(List<WarmingPitcher> pen) =>
            pen.Select(w => new BullpenSnapshot(w.Player.Id, w.IsWarming, w.BattersFacedWarming)).ToList();

        string half = s.IsTopOfInning ? "▲" : "▼";
        return new GameSnapshot
        {
            Description = s.Status == GameStatus.Final
                ? $"{s.VisitorTeam.Abbreviation} {s.VisitorTotalScore}, {s.HomeTeam.Abbreviation} {s.HomeTotalScore} — Final"
                : $"{s.VisitorTeam.Abbreviation} {s.VisitorTotalScore}, {s.HomeTeam.Abbreviation} {s.HomeTotalScore} — {half}{s.CurrentInning}",
            VisitorTeamId = s.VisitorTeam.Id,
            HomeTeamId = s.HomeTeam.Id,
            StadiumId = s.Stadium.Id,
            Options = s.Options,
            VisitorControl = s.VisitorControl,
            HomeControl = s.HomeControl,
            VisitorHintsUsed = s.VisitorHintsUsed,
            HomeHintsUsed = s.HomeHintsUsed,
            VisitorLastHintPlay = s.VisitorLastHintPlay,
            HomeLastHintPlay = s.HomeLastHintPlay,
            VisitorManagerId = s.VisitorManagerId,
            HomeManagerId = s.HomeManagerId,
            Status = s.Status,
            VisitorScoreByInning = [.. s.VisitorScoreByInning],
            HomeScoreByInning = [.. s.HomeScoreByInning],
            CurrentInning = s.CurrentInning,
            IsTopOfInning = s.IsTopOfInning,
            Outs = s.Outs,
            RunnerOnFirst = Runner(s.RunnerOnFirst),
            RunnerOnSecond = Runner(s.RunnerOnSecond),
            RunnerOnThird = Runner(s.RunnerOnThird),
            VisitorLineup = [.. s.VisitorLineup],
            HomeLineup = [.. s.HomeLineup],
            VisitorPositions = new(s.VisitorPositions),
            HomePositions = new(s.HomePositions),
            VisitorBatterIndex = s.VisitorBatterIndex,
            HomeBatterIndex = s.HomeBatterIndex,
            VisitorPitcher = Pitcher(s.VisitorPitcher),
            HomePitcher = Pitcher(s.HomePitcher),
            VisitorBullpen = Pen(s.VisitorBullpen),
            HomeBullpen = Pen(s.HomeBullpen),
            VisitorUsed = [.. s.VisitorUsed],
            HomeUsed = [.. s.HomeUsed],
            VisitorAlignment = s.VisitorAlignment,
            HomeAlignment = s.HomeAlignment,
            PlayByPlay = s.PlayByPlay.Take(MaxLoggedPlays).ToList(),
            // NOTE: Box (and Recap's items) are shared with the live game, not copied: a snapshot that is kept while play
            // goes on would show later plays too. Serialize it straight away (ToJson), as GameSaveStore and CastService do.
            Box = s.Box,
            ErrorThisHalfInning = s.ErrorThisHalfInning,
            EndedByRain = s.EndedByRain,
            RainDelays = s.RainDelays,
            VisitorManagerEjected = s.VisitorManagerEjected,
            HomeManagerEjected = s.HomeManagerEjected,
            PendingInjury = s.PendingInjury,
            Recap = [.. s.Recap],
        };
    }

    /// <summary>Rebuilds a game; throws InvalidOperationException if the save no longer matches the rosters.</summary>
    public static GameState Restore(GameSnapshot snap, Func<int, Team?> findTeam, Func<string, Stadium?>? findStadium = null)
    {
        if (snap.Version != GameSnapshot.CurrentVersion)
            throw new InvalidOperationException("This saved game is from an incompatible version.");

        var visitor = findTeam(snap.VisitorTeamId)
            ?? throw new InvalidOperationException("A team in this saved game no longer exists.");
        var home = findTeam(snap.HomeTeamId)
            ?? throw new InvalidOperationException("A team in this saved game no longer exists.");

        static Player PlayerOf(Team team, int id) =>
            team.Roster.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException($"{team.DisplayName} has changed since this game was saved.");

        BaseRunner? Runner(RunnerSnapshot? r, Team batting) =>
            r is null ? null : new BaseRunner
            {
                Player = PlayerOf(batting, r.PlayerId), BattingSlot = r.BattingSlot,
                ResponsiblePitcherId = r.ResponsiblePitcherId,
            };

        ActivePitcher Pitcher(PitcherSnapshot p, Team team) => new()
        {
            Player = PlayerOf(team, p.PlayerId),
            PitchCount = p.PitchCount,
            BattersFaced = p.BattersFaced,
            SettleFrom = p.SettleFrom,
            RunsAllowed = p.RunsAllowed,
            EntryModifier = p.EntryModifier,
            Condition = p.Condition,
        };

        List<WarmingPitcher> Pen(List<BullpenSnapshot> pen, Team team) => pen.Select(w => new WarmingPitcher
        {
            Player = PlayerOf(team, w.PlayerId),
            IsWarming = w.IsWarming,
            BattersFacedWarming = w.BattersFacedWarming,
        }).ToList();

        void CheckLineup(Team team, List<int> lineup, Dictionary<int, Position> positions)
        {
            foreach (var id in lineup) PlayerOf(team, id);
            if (lineup.Count != LineupRules.LineupSize || lineup.Any(id => !positions.ContainsKey(id)))
                throw new InvalidOperationException("The saved lineup is incomplete.");
        }
        CheckLineup(visitor, snap.VisitorLineup, snap.VisitorPositions);
        CheckLineup(home, snap.HomeLineup, snap.HomePositions);

        // WHY: a snapshot stores only player IDs, and the runners on base always belong to the team at bat, so they are
        // looked up in that team's roster.
        var batting = snap.IsTopOfInning ? visitor : home;
        return new GameState
        {
            VisitorTeam = visitor,
            HomeTeam = home,
            Stadium = findStadium?.Invoke(snap.StadiumId) ?? Stadium.Generic,
            Options = snap.Options,
            DHRule = DesignatedHitterRules.Applies(snap.Options.DesignatedHitter, home),
            VisitorControl = snap.VisitorControl,
            HomeControl = snap.HomeControl,
            VisitorHintsUsed = snap.VisitorHintsUsed,
            HomeHintsUsed = snap.HomeHintsUsed,
            VisitorLastHintPlay = snap.VisitorLastHintPlay,
            HomeLastHintPlay = snap.HomeLastHintPlay,
            VisitorManagerId = snap.VisitorManagerId,
            HomeManagerId = snap.HomeManagerId,
            Status = snap.Status,
            VisitorScoreByInning = [.. snap.VisitorScoreByInning],
            HomeScoreByInning = [.. snap.HomeScoreByInning],
            CurrentInning = snap.CurrentInning,
            IsTopOfInning = snap.IsTopOfInning,
            Outs = snap.Outs,
            RunnerOnFirst = Runner(snap.RunnerOnFirst, batting),
            RunnerOnSecond = Runner(snap.RunnerOnSecond, batting),
            RunnerOnThird = Runner(snap.RunnerOnThird, batting),
            VisitorLineup = [.. snap.VisitorLineup],
            HomeLineup = [.. snap.HomeLineup],
            VisitorPositions = new(snap.VisitorPositions),
            HomePositions = new(snap.HomePositions),
            VisitorBatterIndex = snap.VisitorBatterIndex,
            HomeBatterIndex = snap.HomeBatterIndex,
            VisitorPitcher = Pitcher(snap.VisitorPitcher, visitor),
            HomePitcher = Pitcher(snap.HomePitcher, home),
            VisitorBullpen = Pen(snap.VisitorBullpen, visitor),
            HomeBullpen = Pen(snap.HomeBullpen, home),
            VisitorUsed = [.. snap.VisitorUsed],
            HomeUsed = [.. snap.HomeUsed],
            VisitorAlignment = snap.VisitorAlignment,
            HomeAlignment = snap.HomeAlignment,
            PlayByPlay = [.. snap.PlayByPlay],
            Box = snap.Box,
            ErrorThisHalfInning = snap.ErrorThisHalfInning,
            EndedByRain = snap.EndedByRain,
            RainDelays = snap.RainDelays,
            VisitorManagerEjected = snap.VisitorManagerEjected,
            HomeManagerEjected = snap.HomeManagerEjected,
            PendingInjury = snap.PendingInjury,
            Recap = [.. snap.Recap],
            // WHY: not saved on its own: it is the win probability after the last recorded play, so the next play's swing is
            // measured from the right point. 0.5 for a game that has no plays yet.
            LastHomeWinProbability = snap.Recap.Count > 0
                ? snap.Recap[^1].HomeWinProbability
                : 0.5,
        };
    }
}
