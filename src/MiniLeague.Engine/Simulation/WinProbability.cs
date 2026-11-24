using MiniLeague.Engine.Models;

namespace MiniLeague.Engine.Simulation;

/// <summary>
/// The home team's chance of winning from any game situation, found by working out the odds of every
/// possible score for the rest of the game. It uses league-average scoring, not the two teams' own
/// strengths, so it measures how much a play changed the game rather than who is the better team.
/// </summary>
public static class WinProbability
{
    /// <summary>Average runs scored in a fresh half-inning.</summary>
    public const double FreshHalfInningRuns = 0.49;

    private const int MaxRuns = 14;           // a half-inning's runs beyond this are lumped together
    private const int ExtraInningsModelled = 4;

    // Expected runs from here to the end of the half-inning (the standard "RE24" table).
    // Rows: runners on (bit 1 = first, 2 = second, 4 = third); columns: outs 0..2.
    private static readonly double[,] RunExpectancy =
    {
        { 0.50, 0.27, 0.10 },   // bases empty
        { 0.90, 0.54, 0.23 },   // first
        { 1.14, 0.70, 0.33 },   // second
        { 1.50, 0.95, 0.40 },   // first and second
        { 1.35, 0.95, 0.37 },   // third
        { 1.78, 1.14, 0.49 },   // first and third
        { 1.96, 1.38, 0.54 },   // second and third
        { 2.30, 1.54, 0.76 },   // bases loaded
    };

    public static double ExpectedRuns(int baseState, int outs) =>
        RunExpectancy[Math.Clamp(baseState, 0, 7), Math.Clamp(outs, 0, 2)];

    public static int BaseState(GameState s) =>
        (s.RunnerOnFirst is null ? 0 : 1) | (s.RunnerOnSecond is null ? 0 : 2) | (s.RunnerOnThird is null ? 0 : 4);

    /// <summary>The home team's chance of winning the game as it stands.</summary>
    public static double HomeWins(GameState s)
    {
        int margin = s.HomeTotalScore - s.VisitorTotalScore;
        if (s.Status == GameStatus.Final) return margin > 0 ? 1.0 : margin < 0 ? 0.0 : 0.5;

        return HomeWins(s.CurrentInning, s.IsTopOfInning, s.Outs, BaseState(s), margin);
    }

    /// <param name="margin">Home runs minus visitor runs.</param>
    public static double HomeWins(int inning, bool isTop, int outs, int baseState, int margin,
                                  double freshRuns = FreshHalfInningRuns)
    {
        var current = RunDistribution(ExpectedRuns(baseState, outs));
        var fresh = RunDistribution(freshRuns);

        // diff -> probability, with diff = home minus visitor (offset so it can index an array)
        int range = 3 * MaxRuns + Math.Abs(margin) + 2;
        int offset = range;
        var dist = new double[2 * range + 1];
        dist[Math.Clamp(margin, -range, range) + offset] = 1.0;

        double homeWins = 0, visitorWins = 0;
        int lastInning = Math.Max(9, inning) + ExtraInningsModelled;
        bool first = true;

        for (int i = inning; i <= lastInning; i++)
        {
            for (int half = (first && !isTop) ? 1 : 0; half < 2; half++)   // 0 = top, 1 = bottom
            {
                var runs = first ? current : fresh;
                first = false;
                bool top = half == 0;
                var next = new double[dist.Length];

                for (int d = 0; d < dist.Length; d++)
                {
                    double mass = dist[d];
                    if (mass == 0) continue;
                    for (int r = 0; r <= MaxRuns; r++)
                    {
                        int nd = d + (top ? -r : r);
                        next[Math.Clamp(nd, 0, dist.Length - 1)] += mass * runs[r];
                    }
                }
                dist = next;

                if (i >= 9)
                {
                    if (top)
                    {
                        // the home team wins without batting if it is ahead after the top of the 9th or later
                        for (int d = offset + 1; d < dist.Length; d++) { homeWins += dist[d]; dist[d] = 0; }
                    }
                    else
                    {
                        for (int d = 0; d < dist.Length; d++)
                        {
                            if (d > offset) { homeWins += dist[d]; dist[d] = 0; }
                            else if (d < offset) { visitorWins += dist[d]; dist[d] = 0; }
                        }
                    }
                }
            }
        }

        double tied = dist.Sum();   // still level after the innings modelled: call it even
        double total = homeWins + visitorWins + tied;
        return total <= 0 ? 0.5 : (homeWins + tied / 2) / total;
    }

    // P(runs = k) for the rest of a half-inning with the given average: a scoreless chance, then a geometric tail.
    private static double[] RunDistribution(double expected)
    {
        var p = new double[MaxRuns + 1];
        double zero = Math.Clamp(1 - expected * 0.55, 0.12, 0.97);
        double tail = expected > 0 ? Math.Clamp(1 - (1 - zero) / expected, 0.2, 0.8) : 0.45;

        p[0] = zero;
        double rest = 1 - zero, q = 1.0;
        for (int k = 1; k <= MaxRuns; k++)
        {
            p[k] = k < MaxRuns ? rest * (1 - tail) * q : rest * q;   // the last bin takes the whole remaining tail
            q *= tail;
        }
        return p;
    }
}
