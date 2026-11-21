namespace MiniLeague.Engine.Models;

public class BatterStats
{
    public int AtBats { get; set; }
    public int Hits { get; set; }
    public int Doubles { get; set; }
    public int Triples { get; set; }
    public int HomeRuns { get; set; }
    public int RBI { get; set; }
    public int Walks { get; set; }
    public int Strikeouts { get; set; }
    public int StolenBases { get; set; }
    public int CaughtStealing { get; set; }
    public int HitByPitch { get; set; }

    public double BattingAverage => AtBats > 0 ? (double)Hits / AtBats : 0;
    public double OnBasePercentage
    {
        get
        {
            int pa = AtBats + Walks + HitByPitch;
            return pa > 0 ? (double)(Hits + Walks + HitByPitch) / pa : 0;
        }
    }
    public double SluggingPct
    {
        get
        {
            if (AtBats == 0) return 0;
            int singles = Hits - Doubles - Triples - HomeRuns;
            return (double)(singles + 2 * Doubles + 3 * Triples + 4 * HomeRuns) / AtBats;
        }
    }
}
