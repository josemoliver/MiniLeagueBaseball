namespace MiniLeague.Engine.Models;

/// <summary>One hitter's line. Substitutes share the batting-order <see cref="Slot"/> of the player they replaced.</summary>
public class BatterLine
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Box-score position notation: "SS", "PH-SS" (pinch hitter), "PR-LF" (pinch runner).</summary>
    public string Pos { get; set; } = "";
    public int Slot { get; set; }
    public int AB { get; set; }
    public int R { get; set; }
    public int H { get; set; }
    public int D2 { get; set; }
    public int D3 { get; set; }
    public int HR { get; set; }
    public int RBI { get; set; }
    public int BB { get; set; }
    public int SO { get; set; }
    public int SB { get; set; }
    public int CS { get; set; }
    public int HBP { get; set; }
}

public class PitcherLine
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public int Outs { get; set; }
    public int H { get; set; }
    public int R { get; set; }
    public int ER { get; set; }
    public int BB { get; set; }
    public int SO { get; set; }
    public int HR { get; set; }
    public int BF { get; set; }
    public int Pitches { get; set; }
    /// <summary>His team's lead when he came in (negative if behind); used for saves.</summary>
    public int EntryLead { get; set; }
    /// <summary>"W", "L", "S" or empty.</summary>
    public string Decision { get; set; } = "";

    public string IP => $"{Outs / 3}.{Outs % 3}";
}

public class TeamBox
{
    public List<BatterLine> Batters { get; set; } = [];
    public List<PitcherLine> Pitchers { get; set; } = [];
    public int Errors { get; set; }
    public int LeftOnBase { get; set; }
    public int DoublePlays { get; set; }
    /// <summary>True for a player who took over a batting spot (his line is indented under the starter's).</summary>
    public bool IsSubstitute(BatterLine line) => Batters.TakeWhile(b => b != line).Any(b => b.Slot == line.Slot);

    public int Hits => Batters.Sum(b => b.H);
    public int Runs => Batters.Sum(b => b.R);
    public List<string> ErrorNotes { get; set; } = [];
}

/// <summary>The side that last took the lead for good-so-far, and the pitchers on the hook for it.</summary>
public class GoAhead
{
    public bool VisitorLead { get; set; }
    public int WinnerPitcherId { get; set; }
    public int LoserPitcherId { get; set; }
}

public class BoxScore
{
    public TeamBox Visitor { get; set; } = new();
    public TeamBox Home { get; set; } = new();
    public GoAhead? GoAhead { get; set; }
    /// <summary>Game notes such as injuries, ejections and weather delays, in order.</summary>
    public List<string> Notes { get; set; } = [];

    public TeamBox For(bool visitor) => visitor ? Visitor : Home;
}
