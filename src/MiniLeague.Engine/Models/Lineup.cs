namespace MiniLeague.Engine.Models;

/// <summary>One spot in a batting order: who bats and where he plays in the field.</summary>
public record LineupSlot(int PlayerId, Position Position);

public static class LineupRules
{
    /// <summary>Every lineup must use each of these exactly once (the DH is the ninth hitter).</summary>
    public static readonly Position[] RequiredPositions =
    [
        Position.C, Position.First, Position.Second, Position.Third, Position.Short,
        Position.Left, Position.Center, Position.Right, Position.DH
    ];

    /// <summary>The same, when the pitcher bats: the pitcher takes the place of the designated hitter.</summary>
    public static readonly Position[] PitchersBatPositions =
    [
        Position.C, Position.First, Position.Second, Position.Third, Position.Short,
        Position.Left, Position.Center, Position.Right, Position.P
    ];

    public const int LineupSize = 9;

    public static Position[] RequiredFor(bool designatedHitter) =>
        designatedHitter ? RequiredPositions : PitchersBatPositions;

    /// <summary>
    /// The team's default batting order with each player at his listed position.
    /// Duplicate or missing positions are repaired so the result is always valid.
    /// </summary>
    public static List<LineupSlot> Default(Team team) => Default(team, designatedHitter: true, starter: null);

    /// <summary>
    /// The default order for the rule in force. With a designated hitter it is the team's own. Without, the designated
    /// hitter is dropped and the starting pitcher bats ninth.
    /// </summary>
    public static List<LineupSlot> Default(Team team, bool designatedHitter, Player? starter)
    {
        var withDh = DefaultWithDesignatedHitter(team);
        if (designatedHitter) return withDh;

        var lineup = withDh.Where(s => s.Position != Position.DH).ToList();
        lineup.Add(new LineupSlot((starter ?? team.DefaultStarter).Id, Position.P));
        return lineup;
    }

    /// <summary>
    /// Fits a lineup someone edited to the rule in force: a lineup built for the other rule is dropped for the default,
    /// and when the pitcher bats, the starter chosen now takes the pitcher's spot in the order.
    /// </summary>
    public static List<LineupSlot> Adapt(Team team, IReadOnlyList<LineupSlot>? custom, bool designatedHitter, Player starter)
    {
        if (custom is null) return Default(team, designatedHitter, starter);
        bool hasPitcher = custom.Any(s => s.Position == Position.P);
        bool hasDh = custom.Any(s => s.Position == Position.DH);
        if (designatedHitter ? hasPitcher || !hasDh : hasDh || !hasPitcher)
            return Default(team, designatedHitter, starter);
        if (designatedHitter) return [.. custom];

        // The starter may already be in the order at a fielding position by an earlier edit; two places is never valid.
        if (custom.Any(s => s.PlayerId == starter.Id && s.Position != Position.P))
            return Default(team, designatedHitter, starter);
        return custom.Select(s => s.Position == Position.P ? new LineupSlot(starter.Id, Position.P) : s).ToList();
    }

    private static List<LineupSlot> DefaultWithDesignatedHitter(Team team)
    {
        var ids = team.DefaultLineup.Count > 0
            ? team.DefaultLineup
            : team.Batters.Take(LineupSize).Select(p => p.Id).ToList();

        var taken = new HashSet<Position>();
        var positions = new Position?[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            var pos = team.Roster.First(p => p.Id == ids[i]).PrimaryPosition;
            if (RequiredPositions.Contains(pos) && taken.Add(pos))
                positions[i] = pos;
        }

        var free = new Queue<Position>(RequiredPositions.Where(p => !taken.Contains(p)));
        for (int i = 0; i < positions.Length; i++)
            if (positions[i] is null && free.Count > 0)
                positions[i] = free.Dequeue();

        return ids.Select((id, i) => new LineupSlot(id, positions[i] ?? Position.DH)).ToList();
    }

    /// <summary>Returns a human-readable list of problems; empty means the lineup is valid.</summary>
    public static IReadOnlyList<string> Validate(Team team, IReadOnlyList<LineupSlot> slots) =>
        Validate(team, slots, designatedHitter: true, starterId: null);

    /// <param name="designatedHitter">The rule in force. Without one, the starting pitcher must bat, at P.</param>
    /// <param name="starterId">The starting pitcher, who must be the one in the order when the pitcher bats (any pitcher if null).</param>
    public static IReadOnlyList<string> Validate(Team team, IReadOnlyList<LineupSlot> slots, bool designatedHitter, int? starterId)
    {
        var errors = new List<string>();
        if (slots.Count != LineupSize)
            errors.Add($"A lineup needs exactly {LineupSize} players (has {slots.Count}).");

        foreach (var dup in slots.GroupBy(s => s.PlayerId).Where(g => g.Count() > 1))
        {
            var name = team.Roster.FirstOrDefault(p => p.Id == dup.Key)?.Name ?? dup.Key.ToString();
            errors.Add($"{name} is in the lineup more than once.");
        }

        foreach (var slot in slots)
        {
            var player = team.Roster.FirstOrDefault(p => p.Id == slot.PlayerId);
            if (player is null) errors.Add($"Player {slot.PlayerId} is not on this team.");
            else if (designatedHitter && player.IsPitcher)
                errors.Add($"{player.Name} is a pitcher and cannot be in the batting order.");
            else if (!designatedHitter && player.IsPitcher && slot.Position != Position.P)
                errors.Add($"{player.Name} is a pitcher and can only bat at P.");
            else if (!designatedHitter && !player.IsPitcher && slot.Position == Position.P)
                errors.Add($"{player.Name} is not a pitcher and cannot play P.");
            else if (!designatedHitter && player.IsPitcher && starterId is int sid && player.Id != sid)
                errors.Add($"{player.Name} is not the starting pitcher; the starter must bat in the pitcher's spot.");
        }

        var required = RequiredFor(designatedHitter);
        foreach (var pos in required)
        {
            int n = slots.Count(s => s.Position == pos);
            if (n == 0) errors.Add($"No player is assigned to {Label(pos)}.");
            else if (n > 1) errors.Add($"{Label(pos)} is assigned to {n} players.");
        }

        if (slots.Any(s => !required.Contains(s.Position)))
            errors.Add(designatedHitter
                ? "Pitchers do not bat; use positions C through DH only."
                : "There is no designated hitter; use positions C through RF, and P for the pitcher.");

        return errors;
    }

    public static string Label(Position p) => p switch
    {
        Position.P => "P", Position.C => "C", Position.First => "1B", Position.Second => "2B",
        Position.Third => "3B", Position.Short => "SS", Position.Left => "LF",
        Position.Center => "CF", Position.Right => "RF", Position.DH => "DH", _ => p.ToString()
    };
}
