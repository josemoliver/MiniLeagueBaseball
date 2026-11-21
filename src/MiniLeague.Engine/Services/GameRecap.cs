using System.Text;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Services;

public record GameRecap(
    string Headline,
    IReadOnlyList<RecapPlay> Track,
    IReadOnlyList<RecapPlay> KeyPlays,
    IReadOnlyList<string> Story,
    IReadOnlyList<string> Performers);

/// <summary>Turns a finished (or in-progress) game into highlights and a short written recap.</summary>
public static class GameRecaps
{
    public const int KeyPlayCount = 5;

    /// <summary>The track starts at the first pitch, before any play: an even game.</summary>
    public static double StartingWinProbability(GameState s) => WinProbability.HomeWins(1, true, 0, 0, 0);

    public static RecapPlay RecordPlay(GameState s, PlayResult result, int inning, bool isTop, double before)
    {
        double after = WinProbability.HomeWins(s);
        var play = new RecapPlay(inning, isTop, result.Description, s.VisitorTotalScore, s.HomeTotalScore, after, after - before);
        s.Recap.Add(play);
        s.LastHomeWinProbability = after;
        return play;
    }

    public static GameRecap Build(GameState s)
    {
        var track = s.Recap.ToList();
        var keyPlays = track
            .OrderByDescending(p => Math.Abs(p.Swing))
            .Where(p => Math.Abs(p.Swing) >= 0.04)
            .Take(KeyPlayCount)
            .OrderBy(p => track.IndexOf(p))
            .ToList();

        return new GameRecap(Headline(s), track, keyPlays, Story(s, track), Performers(s));
    }

    private static string Headline(GameState s)
    {
        bool final = s.Status == GameStatus.Final;
        int v = s.VisitorTotalScore, h = s.HomeTotalScore;
        string park = string.IsNullOrEmpty(s.Stadium.Name) ? "" : $" at {s.Stadium.Name}";

        if (!final)
            return $"{s.VisitorTeam.Abbreviation} {v}, {s.HomeTeam.Abbreviation} {h} after {(s.IsTopOfInning ? "the top" : "the middle")} of the {Ordinal(s.CurrentInning)}";

        var winner = h > v ? s.HomeTeam : s.VisitorTeam;
        var loser = h > v ? s.VisitorTeam : s.HomeTeam;
        string score = $"{Math.Max(v, h)}–{Math.Min(v, h)}";
        string length = s.EndedByRain ? $", in a game called by rain"
            : s.CurrentInning > 9 ? $" in {s.CurrentInning} innings" : "";
        return $"{winner.DisplayName} beat the {loser.DisplayName} {score}{length}{park}";
    }

    private static List<string> Story(GameState s, List<RecapPlay> track)
    {
        var story = new List<string>();
        if (track.Count == 0) return story;

        // lead changes and the biggest lead
        int leadChanges = 0, ties = 0, lastLeader = 0, bigVisitor = 0, bigHome = 0;
        RecapPlay? bigVisitorAt = null, bigHomeAt = null;
        foreach (var p in track)
        {
            int margin = p.HomeScore - p.VisitorScore;
            int leader = Math.Sign(margin);
            if (leader != 0 && lastLeader != 0 && leader != lastLeader) leadChanges++;
            if (leader == 0 && lastLeader != 0) ties++;
            if (leader != 0) lastLeader = leader;
            if (-margin > bigVisitor) { bigVisitor = -margin; bigVisitorAt = p; }
            if (margin > bigHome) { bigHome = margin; bigHomeAt = p; }
        }

        bool final = s.Status == GameStatus.Final;
        bool homeWon = s.HomeTotalScore > s.VisitorTotalScore;
        var winnerSide = final ? homeWon : s.HomeTotalScore >= s.VisitorTotalScore;
        string W(bool home) => (home ? s.HomeTeam : s.VisitorTeam).Abbreviation;

        // the biggest comeback
        int trailedBy = winnerSide ? bigVisitor : bigHome;
        var trailedAt = winnerSide ? bigVisitorAt : bigHomeAt;
        if (final && trailedBy >= 3 && trailedAt is not null)
            story.Add($"{W(winnerSide)} came back from {trailedBy} runs down in the {Ordinal(trailedAt.Inning)}.");
        else if (bigHome >= 4 || bigVisitor >= 4)
        {
            bool homeLed = bigHome >= bigVisitor;
            story.Add($"{W(homeLed)} led by {(homeLed ? bigHome : bigVisitor)} at one point.");
        }

        if (leadChanges + ties >= 3)
            story.Add($"A seesaw game: the lead changed hands {leadChanges} time{(leadChanges == 1 ? "" : "s")} and the score was tied {ties} time{(ties == 1 ? "" : "s")}.");
        else if (final && leadChanges == 0 && ties == 0 && track.Count > 0)
            story.Add($"{W(homeWon)} led from the first run to the last.");

        // how close it was for the winner
        if (final)
        {
            double low = winnerSide ? track.Min(p => p.HomeWinProbability) : track.Min(p => 1 - p.HomeWinProbability);
            var lowPlay = winnerSide
                ? track.First(p => p.HomeWinProbability == low)
                : track.First(p => 1 - p.HomeWinProbability == low);
            if (low < 0.2)
                story.Add($"{W(winnerSide)} were down to {Percent(low)} to win in the {Ordinal(lowPlay.Inning)} and still pulled it out.");
        }

        // the turning point
        var biggest = track.OrderByDescending(p => Math.Abs(p.Swing)).First();
        if (Math.Abs(biggest.Swing) >= 0.08)
            story.Add($"The biggest play: {biggest.Description.TrimEnd('.')} in the {biggest.Half}, worth {Percent(Math.Abs(biggest.Swing))} to {W(biggest.Swing > 0)}.");

        // The home team only wins in the bottom of the 9th or later by taking the lead mid-inning: a walk-off.
        if (final && !s.EndedByRain && homeWon && !s.IsTopOfInning && s.CurrentInning >= 9)
            story.Add($"It ended with a walk-off: {track[^1].Description.TrimEnd('.')}.");

        // fielding and pitching
        foreach (bool visitor in new[] { true, false })
        {
            int errors = s.Box.For(visitor).Errors;
            if (errors >= 3) story.Add($"{s.TeamFor(visitor).Abbreviation} committed {errors} errors.");
        }
        foreach (var p in s.Box.Visitor.Pitchers.Concat(s.Box.Home.Pitchers))
        {
            if (p.Decision == "W") story.Add($"Winning pitcher: {p.Name}, {p.IP} innings, {p.R} run{(p.R == 1 ? "" : "s")}, {p.SO} strikeout{(p.SO == 1 ? "" : "s")}.");
            if (p.Decision == "L") story.Add($"Losing pitcher: {p.Name}, {p.R} run{(p.R == 1 ? "" : "s")} in {p.IP} innings.");
            if (p.Decision == "S") story.Add($"{p.Name} earned the save.");
        }
        if (s.EndedByRain) story.Add("Rain ended the game early.");
        return story;
    }

    /// <summary>The hitters (and pitchers) who stood out, best first.</summary>
    private static List<string> Performers(GameState s)
    {
        var lines = new List<(double Score, string Text)>();
        foreach (bool visitor in new[] { true, false })
        {
            string team = s.TeamFor(visitor).Abbreviation;
            foreach (var b in s.Box.For(visitor).Batters.Where(b => b.AB + b.BB > 0))
            {
                int tb = b.H + b.D2 + 2 * b.D3 + 3 * b.HR;
                double score = tb + b.RBI + 0.5 * (b.R + b.BB + b.SB) - 0.25 * (b.AB - b.H);
                var parts = new List<string> { $"{b.H}-for-{b.AB}" };
                if (b.HR > 0) parts.Add(b.HR == 1 ? "HR" : $"{b.HR} HR");
                if (b.D3 > 0) parts.Add(b.D3 == 1 ? "3B" : $"{b.D3} 3B");
                if (b.D2 > 0) parts.Add(b.D2 == 1 ? "2B" : $"{b.D2} 2B");
                if (b.RBI > 0) parts.Add($"{b.RBI} RBI");
                if (b.SB > 0) parts.Add($"{b.SB} SB");
                if (b.BB > 0) parts.Add($"{b.BB} BB");
                lines.Add((score, $"{b.Name} ({team}): {string.Join(", ", parts)}"));
            }
        }
        return lines.OrderByDescending(l => l.Score).Take(3).Where(l => l.Score >= 1.5).Select(l => l.Text).ToList();
    }

    /// <summary>A probability as a whole-number percentage such as "53%", the same in every culture.</summary>
    public static string Percent(double value) =>
        Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string Ordinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th",
    };

    // ── Plain text, for copying and sharing ──────────────────────────────────

    public static string ToText(GameState s)
    {
        var recap = Build(s);
        var sb = new StringBuilder();
        sb.AppendLine(recap.Headline);
        sb.AppendLine(BoxScoreFormatter.Result(s));
        foreach (var line in recap.Story) sb.AppendLine("• " + line);

        if (recap.Performers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Top performers");
            foreach (var p in recap.Performers) sb.AppendLine("• " + p);
        }

        if (recap.KeyPlays.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Highlights");
            foreach (var p in recap.KeyPlays)
            {
                var team = (p.Swing > 0 ? s.HomeTeam : s.VisitorTeam).Abbreviation;
                sb.AppendLine($"• {p.Half}  {s.VisitorTeam.Abbreviation} {p.VisitorScore}, {s.HomeTeam.Abbreviation} {p.HomeScore}  " +
                              $"{p.Description}  ({(p.Swing > 0 ? "+" : "-")}{Percent(Math.Abs(p.Swing))} {team})");
            }
        }
        return sb.ToString();
    }
}
