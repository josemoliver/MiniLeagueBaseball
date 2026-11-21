using System.Text;
using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Services;

/// <summary>Lays a game out as a classic newspaper-style box score in plain text.</summary>
public static class BoxScoreFormatter
{
    public static string Result(GameState s)
    {
        string status = s.Status switch
        {
            GameStatus.Final when s.EndedByRain => " (called, rain)",
            GameStatus.Final when s.CurrentInning != 9 => $" ({s.CurrentInning} innings)",
            GameStatus.Final => "",
            _ => $" ({(s.IsTopOfInning ? "top" : "bottom")} {s.CurrentInning})",
        };
        return $"{s.VisitorTeam.DisplayName} {s.VisitorTotalScore}, {s.HomeTeam.DisplayName} {s.HomeTotalScore}{status}";
    }

    public static string ToText(GameState s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Result(s));
        if (!string.IsNullOrEmpty(s.Stadium.Name)) sb.AppendLine(s.Stadium.Name);
        sb.AppendLine();
        AppendLineScore(sb, s);

        foreach (bool visitor in new[] { true, false })
        {
            sb.AppendLine();
            AppendBatting(sb, s, visitor);
            AppendPitching(sb, s, visitor);
        }

        if (s.Box.Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var note in s.Box.Notes) sb.AppendLine(note);
        }
        return sb.ToString();
    }

    private static void AppendLineScore(StringBuilder sb, GameState s)
    {
        int innings = Math.Max(9, s.CurrentInning);
        sb.Append("".PadRight(6));
        for (int i = 1; i <= innings; i++) sb.Append(i.ToString().PadLeft(3));
        sb.AppendLine("   R  H  E");

        foreach (bool visitor in new[] { true, false })
        {
            var team = s.TeamFor(visitor);
            sb.Append(team.Abbreviation.PadRight(6));
            for (int i = 1; i <= innings; i++)
            {
                sb.Append(s.LineScoreCell(visitor, i).PadLeft(3));
            }
            var box = s.Box.For(visitor);
            sb.AppendLine($"{(visitor ? s.VisitorTotalScore : s.HomeTotalScore),4}{box.Hits,3}{box.Errors,3}");
        }
    }

    private static void AppendBatting(StringBuilder sb, GameState s, bool visitor)
    {
        var team = s.TeamFor(visitor);
        var box = s.Box.For(visitor);
        sb.AppendLine($"{team.DisplayName.ToUpperInvariant(),-28}AB  R  H RBI BB SO");

        foreach (var line in box.Batters.OrderBy(b => b.Slot))
        {
            string label = $"{(box.IsSubstitute(line) ? "  " : "")}{line.Name} {line.Pos.ToLowerInvariant()}";
            sb.AppendLine($"{label,-28}{line.AB,2}{line.R,3}{line.H,3}{line.RBI,4}{line.BB,3}{line.SO,3}");
        }

        var b = box.Batters;
        sb.AppendLine($"{"Totals",-28}{b.Sum(x => x.AB),2}{b.Sum(x => x.R),3}{b.Sum(x => x.H),3}{b.Sum(x => x.RBI),4}{b.Sum(x => x.BB),3}{b.Sum(x => x.SO),3}");

        AppendList(sb, "2B", b.Where(x => x.D2 > 0).Select(x => Counted(x.Name, x.D2)));
        AppendList(sb, "3B", b.Where(x => x.D3 > 0).Select(x => Counted(x.Name, x.D3)));
        AppendList(sb, "HR", b.Where(x => x.HR > 0).Select(x => Counted(x.Name, x.HR)));
        AppendList(sb, "SB", b.Where(x => x.SB > 0).Select(x => Counted(x.Name, x.SB)));
        AppendList(sb, "CS", b.Where(x => x.CS > 0).Select(x => Counted(x.Name, x.CS)));
        AppendList(sb, "HBP", b.Where(x => x.HBP > 0).Select(x => Counted(x.Name, x.HBP)));
        sb.AppendLine($"LOB: {box.LeftOnBase}.  E: {(box.Errors == 0 ? "none" : string.Join(", ", box.ErrorNotes))}.  DP: {box.DoublePlays}.");
    }

    private static void AppendPitching(StringBuilder sb, GameState s, bool visitor)
    {
        var team = s.TeamFor(visitor);
        sb.AppendLine();
        sb.AppendLine($"{team.Abbreviation + " PITCHING",-28}  IP  H  R ER BB SO");
        foreach (var p in s.Box.For(visitor).Pitchers)
        {
            string name = p.Decision.Length > 0 ? $"{p.Name} ({p.Decision})" : p.Name;
            sb.AppendLine($"{name,-28}{p.IP,4}{p.H,3}{p.R,3}{p.ER,3}{p.BB,3}{p.SO,3}");
        }
        var pit = s.Box.For(visitor).Pitchers;
        AppendList(sb, "HR allowed", pit.Where(x => x.HR > 0).Select(x => Counted(x.Name, x.HR)));
        AppendList(sb, "Pitches-BF", pit.Select(x => $"{x.Name} {x.Pitches}-{x.BF}"));
    }

    private static string Counted(string name, int n) => n > 1 ? $"{name} {n}" : name;

    private static void AppendList(StringBuilder sb, string label, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count > 0) sb.AppendLine($"{label}: {string.Join(", ", list)}.");
    }
}
