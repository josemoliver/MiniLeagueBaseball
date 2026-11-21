using MiniLeague.Engine.Models;

namespace MiniLeague.Web.Components.Game;

/// <summary>Short text summaries of players for selects and lineup cards.</summary>
public static class PlayerLabels
{
    public static string Average(BatterStats? b)
    {
        if (b is null || b.AtBats == 0) return ".---";
        string raw = b.BattingAverage.ToString("F3");
        return raw.StartsWith('0') ? raw[1..] : raw;
    }

    /// <summary>e.g. "B. Ruth  .356 · 60 HR · SPD 3 · L"</summary>
    public static string Line(Player p) =>
        $"{p.Name}  {Average(p.Overall)} · {p.Overall?.HomeRuns ?? 0} HR · SPD {p.SpeedRating} · {Hand(p.BatHand)}";

    /// <summary>e.g. "W. Hoyt — 22-7 · 2.63 ERA · 256 IP", with the role for anyone who is not a starter.</summary>
    public static string Pitcher(Player p)
    {
        var s = p.PitchingOverall;
        string role = p.Role is PitcherRole.Starter or null ? "" : $" ({p.Role})";
        return s is null
            ? p.Name + role
            : $"{p.Name}{role} — {s.Wins}-{s.Losses} · {s.ERA:F2} ERA · {s.InningsPitched:0} IP";
    }

    /// <summary>e.g. "range 6 · glove 2 · arm 4", or a warning when he is out of position.</summary>
    public static string Fielding(Player p, Position pos)
    {
        if (pos == Position.DH) return "designated hitter";
        var f = p.FieldingAt(pos);
        string ratings = $"range {f.Range} · glove {f.ErrorRating} · arm {f.Arm}";
        return p.IsNaturalAt(pos) ? ratings : $"⚠ out of position — {ratings}";
    }

    public static string Hand(BatHand b) => b switch
    {
        BatHand.Left => "L", BatHand.Right => "R", _ => "S"
    };
}
