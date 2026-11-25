namespace MiniLeague.Engine.Models;

public enum ManagerControl { Human, Computer }
public enum GameStatus { Setup, InProgress, Final, Suspended }
public enum PitchType { Fastball, Curveball, Slider, Changeup, Pitchout }
public enum OffensiveStrategy { SwingAway, SwingAgressive, SwingSafe, Steal, HitAndRun, SacrificeBunt, SurpriseBunt, Squeeze, SafetySqueeze }
public enum DefensiveAlignment { Standard, CornersIn, InfieldIn, GuardLines, ShiftPull, ShiftOpposite }

public class ActivePitcher
{
    public Player Player { get; set; } = null!;
    public int PitchCount { get; set; }
    public int BattersFaced { get; set; }
    /// <summary>Batters faced when he last had to settle in (entering the game, or after a rain delay).</summary>
    public int SettleFrom { get; set; }
    public int RunsAllowed { get; set; }
    public int InningsRecorded { get; set; }  // outs / 3
    public int OutsThisInning { get; set; }
    public PitcherCondition Condition { get; set; } = PitcherCondition.Fresh;

    /// <summary>
    /// Effectiveness while settling in: a reliever who was cold or overwarmed when called
    /// pitches below par for his first few batters (see FatigueModel).
    /// </summary>
    public double EntryModifier { get; set; } = 1.0;
}

public enum PitcherCondition { Fresh, Ok, Tiring, Tired, Exhausted }

public class WarmingPitcher
{
    public Player Player { get; set; } = null!;

    /// <summary>True once the manager has told him to warm up; he then warms as batters are faced.</summary>
    public bool IsWarming { get; set; }
    public int BattersFacedWarming { get; set; }
    public bool IsWarm => BattersFacedWarming is >= 4 and <= 10;
    public bool IsCold => BattersFacedWarming < 4;
    public bool IsOverwarmed => BattersFacedWarming > 10;

    public string Status => !IsWarming && BattersFacedWarming == 0 ? "Resting"
        : IsWarm ? "Ready"
        : IsOverwarmed ? "Overwarmed"
        : $"Warming ({BattersFacedWarming}/4)";
}

public class BaseRunner
{
    public Player Player { get; set; } = null!;
    public int BattingSlot { get; set; }
    /// <summary>The pitcher charged if he scores (0 until the runner reaches base).</summary>
    public int ResponsiblePitcherId { get; set; }
}

public class PlayEvent
{
    public int Inning { get; set; }
    public bool IsTop { get; set; }
    public string Description { get; set; } = string.Empty;
    public int VisitorScore { get; set; }
    public int HomeScore { get; set; }
}

public class GameState
{
    public Guid GameId { get; set; } = Guid.NewGuid();
    public Team VisitorTeam { get; set; } = null!;
    public Team HomeTeam { get; set; } = null!;
    public Stadium Stadium { get; set; } = Stadium.Generic;
    public bool DHRule { get; set; }

    // Control
    public ManagerControl VisitorControl { get; set; }
    public ManagerControl HomeControl { get; set; }

    /// <summary>
    /// Which computer manager (a <c>ManagerPersona</c> id) runs each side while it is computer-controlled.
    /// Null means the default manager; it is saved with the game so a resumed game keeps the same personalities.
    /// </summary>
    public string? VisitorManagerId { get; set; }
    public string? HomeManagerId { get; set; }

    // Score
    // One entry per inning; grows past nine in extra innings.
    public List<int> VisitorScoreByInning { get; set; } = new(new int[9]);
    public List<int> HomeScoreByInning { get; set; } = new(new int[9]);
    public int VisitorTotalScore => VisitorScoreByInning.Sum();
    public int HomeTotalScore => HomeScoreByInning.Sum();

    // Inning state
    public int CurrentInning { get; set; } = 1;   // 1-based
    public bool IsTopOfInning { get; set; } = true;
    public int Outs { get; set; }

    // Base runners (null = empty)
    public BaseRunner? RunnerOnFirst { get; set; }
    public BaseRunner? RunnerOnSecond { get; set; }
    public BaseRunner? RunnerOnThird { get; set; }
    public bool BasesLoaded => RunnerOnFirst is not null && RunnerOnSecond is not null && RunnerOnThird is not null;

    // Lineups — indices into team roster
    public List<int> VisitorLineup { get; set; } = [];   // player IDs in order
    public List<int> HomeLineup { get; set; } = [];
    public int VisitorBatterIndex { get; set; }   // position in lineup (0–8)
    public int HomeBatterIndex { get; set; }

    // Where each lineup player is playing in the field (player ID → position)
    public Dictionary<int, Position> VisitorPositions { get; set; } = [];
    public Dictionary<int, Position> HomePositions { get; set; } = [];

    // Active pitchers
    public ActivePitcher VisitorPitcher { get; set; } = null!;
    public ActivePitcher HomePitcher { get; set; } = null!;

    // Bullpen warm-up
    public List<WarmingPitcher> VisitorBullpen { get; set; } = [];
    public List<WarmingPitcher> HomeBullpen { get; set; } = [];

    // Substitution tracking (player IDs that have been used/removed)
    public HashSet<int> VisitorUsed { get; set; } = [];
    public HashSet<int> HomeUsed { get; set; } = [];

    // Defensive alignment
    public DefensiveAlignment VisitorAlignment { get; set; }
    public DefensiveAlignment HomeAlignment { get; set; }

    // Play-by-play log
    public List<PlayEvent> PlayByPlay { get; set; } = [];

    public GameStatus Status { get; set; } = GameStatus.Setup;

    // Random events and the box score
    public GameOptions Options { get; set; } = GameOptions.None;
    public BoxScore Box { get; set; } = new();
    /// <summary>An error has been made in the current half-inning: runs from here on are unearned.</summary>
    public bool ErrorThisHalfInning { get; set; }
    public bool EndedByRain { get; set; }
    public int RainDelays { get; set; }
    public bool VisitorManagerEjected { get; set; }
    public bool HomeManagerEjected { get; set; }
    public InjuryNotice? PendingInjury { get; set; }

    /// <summary>Every play with the home team's win probability after it (for the recap and highlights).</summary>
    public List<RecapPlay> Recap { get; set; } = [];
    public double LastHomeWinProbability { get; set; } = 0.5;

    /// <summary>The home team's chance of winning right now.</summary>
    public double HomeWinProbability => MiniLeague.Engine.Simulation.WinProbability.HomeWins(this);

    public void AddRuns(bool visitor, int inning, int runs)
    {
        var line = visitor ? VisitorScoreByInning : HomeScoreByInning;
        while (line.Count < inning) line.Add(0);
        line[inning - 1] += runs;
    }

    /// <summary>Runs a side scored in an inning (0 for innings not yet played or without a run).</summary>
    public int RunsInInning(bool visitor, int inning)
    {
        var line = visitor ? VisitorScoreByInning : HomeScoreByInning;
        return inning >= 1 && inning <= line.Count ? line[inning - 1] : 0;
    }

    /// <summary>What a line score shows for a team in an inning: runs, "-" if not reached yet, "X" if not needed.</summary>
    public string LineScoreCell(bool visitor, int inning)
    {
        if (visitor) return inning <= CurrentInning ? RunsInInning(true, inning).ToString() : "-";
        if (inning < CurrentInning) return RunsInInning(false, inning).ToString();
        if (inning > CurrentInning) return "-";
        if (HomeDidNotBatLastInning) return "X";
        return IsTopOfInning ? "-" : RunsInInning(false, inning).ToString();
    }

    /// <summary>Final because the home team led after the top of the 9th or later (no bottom half).</summary>
    public bool HomeDidNotBatLastInning => Status == GameStatus.Final && IsTopOfInning && !EndedByRain;

    // Convenience
    public Team OffenseTeam => IsTopOfInning ? VisitorTeam : HomeTeam;
    public Team DefenseTeam => IsTopOfInning ? HomeTeam : VisitorTeam;
    public ActivePitcher CurrentPitcher => IsTopOfInning ? HomePitcher : VisitorPitcher;
    public int CurrentBatterIndex => IsTopOfInning ? VisitorBatterIndex : HomeBatterIndex;

    public Team TeamFor(bool visitor) => visitor ? VisitorTeam : HomeTeam;
    public List<int> LineupFor(bool visitor) => visitor ? VisitorLineup : HomeLineup;
    public Dictionary<int, Position> PositionsFor(bool visitor) => visitor ? VisitorPositions : HomePositions;
    public HashSet<int> UsedFor(bool visitor) => visitor ? VisitorUsed : HomeUsed;
    public int BatterIndexFor(bool visitor) => visitor ? VisitorBatterIndex : HomeBatterIndex;

    /// <summary>Batters not in the lineup who have not already been removed from the game.</summary>
    public IEnumerable<Player> Bench(bool visitor)
    {
        var lineup = LineupFor(visitor);
        var used = UsedFor(visitor);
        return TeamFor(visitor).Batters.Where(p => !lineup.Contains(p.Id) && !used.Contains(p.Id));
    }

    public ActivePitcher PitcherFor(bool visitor) => visitor ? VisitorPitcher : HomePitcher;
    public List<WarmingPitcher> BullpenFor(bool visitor) => visitor ? VisitorBullpen : HomeBullpen;

    /// <summary>Lineup players of a side, in batting order, with their current positions.</summary>
    public IEnumerable<(Player Player, Position Position)> LineupPlayers(bool visitor)
    {
        var team = TeamFor(visitor);
        var positions = PositionsFor(visitor);
        return LineupFor(visitor).Select(id => (team.Roster.First(p => p.Id == id), positions[id]));
    }

    /// <summary>The player currently fielding the given position for the defense (not the pitcher).</summary>
    public Player? DefenderAt(Position pos)
    {
        var positions = PositionsFor(!IsTopOfInning);
        var id = positions.FirstOrDefault(kv => kv.Value == pos, new(-1, pos)).Key;
        return id < 0 ? null : DefenseTeam.Roster.FirstOrDefault(p => p.Id == id);
    }

    public Position CurrentBatterPosition => PositionsFor(IsTopOfInning)[CurrentBatter.Id];

    public Player CurrentBatter
    {
        get
        {
            var lineup = IsTopOfInning ? VisitorLineup : HomeLineup;
            var team = IsTopOfInning ? VisitorTeam : HomeTeam;
            var id = lineup[CurrentBatterIndex % lineup.Count];
            return team.Roster.First(p => p.Id == id);
        }
    }
}
