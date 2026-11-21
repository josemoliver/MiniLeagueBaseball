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

    public const int LineupSize = 9;

    /// <summary>
    /// The team's default batting order with each player at his listed position.
    /// Duplicate or missing positions are repaired so the result is always valid.
    /// </summary>
    public static List<LineupSlot> Default(Team team)
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
    public static IReadOnlyList<string> Validate(Team team, IReadOnlyList<LineupSlot> slots)
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
            else if (player.IsPitcher) errors.Add($"{player.Name} is a pitcher and cannot be in the batting order.");
        }

        foreach (var pos in RequiredPositions)
        {
            int n = slots.Count(s => s.Position == pos);
            if (n == 0) errors.Add($"No player is assigned to {Label(pos)}.");
            else if (n > 1) errors.Add($"{Label(pos)} is assigned to {n} players.");
        }

        if (slots.Any(s => !RequiredPositions.Contains(s.Position)))
            errors.Add("Pitchers do not bat; use positions C through DH only.");

        return errors;
    }

    public static string Label(Position p) => p switch
    {
        Position.P => "P", Position.C => "C", Position.First => "1B", Position.Second => "2B",
        Position.Third => "3B", Position.Short => "SS", Position.Left => "LF",
        Position.Center => "CF", Position.Right => "RF", Position.DH => "DH", _ => p.ToString()
    };
}
