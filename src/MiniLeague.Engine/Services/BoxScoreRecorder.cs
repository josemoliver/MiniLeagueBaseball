using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Services;

/// <summary>
/// Builds the box score as the game is played. Stats are attributed from each <see cref="PlayResult"/>
/// and the base state before it, so call <see cref="RecordPlay"/> before the play is applied.
/// </summary>
public static class BoxScoreRecorder
{
    public static void StartGame(GameState s)
    {
        s.Box = new BoxScore();
        foreach (bool visitor in new[] { true, false })
        {
            var lineup = s.LineupFor(visitor);
            var positions = s.PositionsFor(visitor);
            var team = s.TeamFor(visitor);
            for (int slot = 0; slot < lineup.Count; slot++)
            {
                var p = team.Roster.First(x => x.Id == lineup[slot]);
                s.Box.For(visitor).Batters.Add(new BatterLine
                {
                    PlayerId = p.Id, Name = p.Name, Slot = slot, Pos = LineupRules.Label(positions[p.Id]),
                });
            }
            Pitcher(s, visitor, s.PitcherFor(visitor).Player, 0);
        }
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    public static BatterLine Batter(GameState s, bool visitor, Player p)
    {
        var box = s.Box.For(visitor);
        var line = box.Batters.FirstOrDefault(b => b.PlayerId == p.Id);
        if (line is not null) return line;

        int slot = s.LineupFor(visitor).IndexOf(p.Id);
        line = new BatterLine { PlayerId = p.Id, Name = p.Name, Slot = Math.Max(slot, 0) };
        box.Batters.Add(line);
        return line;
    }

    public static PitcherLine Pitcher(GameState s, bool visitor, Player p, int entryLead)
    {
        var box = s.Box.For(visitor);
        var line = box.Pitchers.FirstOrDefault(x => x.PlayerId == p.Id);
        if (line is not null) return line;

        line = new PitcherLine { PlayerId = p.Id, Name = p.Name, EntryLead = entryLead };
        box.Pitchers.Add(line);
        return line;
    }

    /// <summary>A substitute takes over a lineup spot; his line goes in with "PH-", "PR-" or a plain position.</summary>
    public static void Substituted(GameState s, bool visitor, Player incoming, string role)
    {
        var line = Batter(s, visitor, incoming);
        var pos = LineupRules.Label(s.PositionsFor(visitor)[incoming.Id]);
        line.Pos = role.Length > 0 ? $"{role}-{pos}" : pos;
    }

    public static void PositionChanged(GameState s, bool visitor, Player player)
    {
        var line = Batter(s, visitor, player);
        var pos = LineupRules.Label(s.PositionsFor(visitor)[player.Id]);
        int dash = line.Pos.IndexOf('-');
        line.Pos = dash >= 0 ? $"{line.Pos[..dash]}-{pos}" : pos;
    }

    public static void Note(GameState s, string text) => s.Box.Notes.Add(text);

    // ── Plays ────────────────────────────────────────────────────────────────

    /// <summary>Credit a plate appearance (or steal attempt) to the batter, runners and pitchers involved.</summary>
    public static void RecordPlay(GameState s, PlayResult r)
    {
        bool offenseVisitor = s.IsTopOfInning;
        var offense = s.Box.For(offenseVisitor);
        var defense = s.Box.For(!offenseVisitor);
        var batter = s.CurrentBatter;
        var pitcher = s.CurrentPitcher;
        var batterLine = Batter(s, offenseVisitor, batter);
        var pitcherLine = Pitcher(s, !offenseVisitor, pitcher.Player, 0);

        bool plateAppearance = r.Outcome is not (OutcomeType.StolenBase or OutcomeType.CaughtStealing);
        pitcherLine.Pitches += r.PitchesThrown;
        pitcherLine.Outs += r.OutsRecorded;

        if (plateAppearance)
        {
            pitcherLine.BF++;
            CreditBatter(batterLine, pitcherLine, r);
        }
        else if (s.RunnerOnFirst is { } runner)
        {
            var runnerLine = Batter(s, offenseVisitor, runner.Player);
            if (r.Outcome == OutcomeType.StolenBase) runnerLine.SB++; else runnerLine.CS++;
        }

        // A hit and run in which the batter struck out and the runner went on the pitch is a steal attempt as well:
        // a stolen base if he reached second, caught stealing if he was thrown out.
        if (r.HitAndRun && r.Outcome == OutcomeType.Strikeout && s.RunnerOnFirst is { } goer)
        {
            var goerLine = Batter(s, offenseVisitor, goer.Player);
            if (r.OutsRecorded >= 2) goerLine.CS++; else goerLine.SB++;
        }

        if (r.Outcome == OutcomeType.Error)
        {
            defense.Errors++;
            var where = r.ErrorPosition is { } pos ? LineupRules.Label(pos) : "";
            defense.ErrorNotes.Add($"{where} {r.ErrorBy?.Name}".Trim());
        }
        // WHY: two or more outs on one play is a double play whatever it is called: a line drive that doubles off the runner
        // and a strikeout that is also a caught steal count, as in the official box score.
        if (r.Outcome is OutcomeType.DoublePlay or OutcomeType.TriplePlay || r.OutsRecorded >= 2)
            defense.DoublePlays++;

        CreditRuns(s, r, offenseVisitor, batter, plateAppearance, pitcher);
    }

    private static void CreditBatter(BatterLine b, PitcherLine p, PlayResult r)
    {
        int rbi = r.Outcome is OutcomeType.DoublePlay or OutcomeType.TriplePlay or OutcomeType.Error ? 0 : r.RunsScored;
        switch (r.Outcome)
        {
            case OutcomeType.Single: b.AB++; b.H++; p.H++; break;
            case OutcomeType.Double: b.AB++; b.H++; b.D2++; p.H++; break;
            case OutcomeType.Triple: b.AB++; b.H++; b.D3++; p.H++; break;
            case OutcomeType.HomeRun: b.AB++; b.H++; b.HR++; p.H++; p.HR++; break;
            case OutcomeType.Walk:
            case OutcomeType.IntentionalWalk: b.BB++; p.BB++; break;
            case OutcomeType.HitByPitch: b.HBP++; break;
            case OutcomeType.Strikeout: b.AB++; b.SO++; p.SO++; break;
            case OutcomeType.SacrificeBunt: b.SH++; break;   // a plate appearance, but not an at-bat
            default: b.AB++; break;   // outs, errors and fielder's choices
        }
        b.RBI += rbi;
    }

    // The runs go to the lead runners first: whoever is on base now but not afterwards (and was not put out) scored.
    private static void CreditRuns(GameState s, PlayResult r, bool offenseVisitor, Player batter,
                                   bool plateAppearance, ActivePitcher pitcher)
    {
        if (r.RunsScored <= 0) return;

        var batterRunner = new BaseRunner { Player = batter, ResponsiblePitcherId = pitcher.Player.Id };
        var candidates = new List<BaseRunner>();
        if (s.RunnerOnThird is not null) candidates.Add(s.RunnerOnThird);
        if (s.RunnerOnSecond is not null) candidates.Add(s.RunnerOnSecond);
        if (s.RunnerOnFirst is not null) candidates.Add(s.RunnerOnFirst);
        if (plateAppearance) candidates.Add(batterRunner);

        var stillOn = new HashSet<int>(new[] { r.NewFirst, r.NewSecond, r.NewThird }
            .Where(x => x is not null).Select(x => x!.Player.Id));
        var scorers = candidates.Where(c => !stillOn.Contains(c.Player.Id)).Take(r.RunsScored);

        bool unearned = r.Outcome == OutcomeType.Error || s.ErrorThisHalfInning;
        var defenseBox = s.Box.For(!offenseVisitor);
        foreach (var scorer in scorers)
        {
            Batter(s, offenseVisitor, scorer.Player).R++;

            int charged = scorer.ResponsiblePitcherId != 0 ? scorer.ResponsiblePitcherId : pitcher.Player.Id;
            var line = defenseBox.Pitchers.FirstOrDefault(x => x.PlayerId == charged)
                ?? Pitcher(s, !offenseVisitor, pitcher.Player, 0);
            line.R++;
            if (!unearned) line.ER++;
        }
    }

    /// <summary>Call as the third out is made: runners still on base were left stranded.</summary>
    public static void HalfInningEnded(GameState s)
    {
        int stranded = (s.RunnerOnFirst is null ? 0 : 1) + (s.RunnerOnSecond is null ? 0 : 1)
                     + (s.RunnerOnThird is null ? 0 : 1);
        s.Box.For(s.IsTopOfInning).LeftOnBase += stranded;
    }

    // ── Lead changes and decisions ───────────────────────────────────────────

    /// <summary>Track who would get the win and loss if the game ended now.</summary>
    public static void TrackLead(GameState s, int marginBefore)
    {
        int margin = s.VisitorTotalScore - s.HomeTotalScore;   // positive: visitors lead
        if (margin == 0)
        {
            s.Box.GoAhead = null;
        }
        else if (margin > 0 && marginBefore <= 0)
        {
            s.Box.GoAhead = new GoAhead
            {
                VisitorLead = true,
                WinnerPitcherId = s.VisitorPitcher.Player.Id,
                LoserPitcherId = s.HomePitcher.Player.Id,
            };
        }
        else if (margin < 0 && marginBefore >= 0)
        {
            s.Box.GoAhead = new GoAhead
            {
                VisitorLead = false,
                WinnerPitcherId = s.HomePitcher.Player.Id,
                LoserPitcherId = s.VisitorPitcher.Player.Id,
            };
        }
    }

    public static void EntryLead(GameState s, bool visitor, PitcherLine line)
    {
        line.EntryLead = visitor
            ? s.VisitorTotalScore - s.HomeTotalScore
            : s.HomeTotalScore - s.VisitorTotalScore;
    }

    /// <summary>Hand out the win, loss and save once the game is over.</summary>
    public static void Finish(GameState s)
    {
        var goAhead = s.Box.GoAhead;
        if (goAhead is null || s.EndedByRain && s.VisitorTotalScore == s.HomeTotalScore) return;

        var win = s.Box.For(goAhead.VisitorLead);
        var lose = s.Box.For(!goAhead.VisitorLead);

        var winner = win.Pitchers.FirstOrDefault(p => p.PlayerId == goAhead.WinnerPitcherId);
        var starter = win.Pitchers.FirstOrDefault();
        // A starter needs five innings to get the win; otherwise the best reliever after him does.
        if (winner is not null && winner == starter && winner.Outs < 15 && win.Pitchers.Count > 1)
        {
            winner = win.Pitchers.Skip(1).OrderByDescending(p => p.Outs).First();
        }
        if (winner is not null) winner.Decision = "W";

        var loser = lose.Pitchers.FirstOrDefault(p => p.PlayerId == goAhead.LoserPitcherId);
        if (loser is not null) loser.Decision = "L";

        var finisher = win.Pitchers.LastOrDefault();
        if (finisher is not null && finisher != winner && finisher != starter
            && finisher.Outs >= 3
            && (finisher.EntryLead is > 0 and <= 3 || finisher.EntryLead > 3 && finisher.Outs >= 9))
            finisher.Decision = "S";
    }
}
