using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// Every call a manager can make must cost something somewhere, or it is simply always right and the managers (and
/// the player) learn to make it every time. Covers aggressive running, the defensive alignments and the pitchout.
/// </summary>
public class NoFreeOptionTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    // The visitors bat in the top of the first; the home team fields.
    private static GameState Game()
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), ManagerControl.Computer, ManagerControl.Computer,
            options: GameOptions.None);
        return game.State!;
    }

    private static Player Runner(int speed) => new() { Id = 900 + speed, Name = $"Runner{speed}", SpeedRating = speed };

    // ── Aggressive running ───────────────────────────────────────────────────

    [Fact]
    public void ARunnerSentHardIsSometimesThrownOut_AndOneWhoIsNotSentNeverIs()
    {
        int sentOut = 0, scored = 0, heldOut = 0;
        const int n = 4000;
        for (int seed = 0; seed < n; seed++)
        {
            var second = new BaseRunner { Player = Runner(3) };
            var (_, _, third, runs) = BaseRunningEngine.Advance(OutcomeType.Single, null, second, null,
                Runner(3), OffensiveStrategy.SwingAgressive, new Random(seed), out var thrown);
            if (thrown is { } t) { sentOut++; Assert.Equal((2, 4), (t.FromBase, t.AtBase)); Assert.Null(third); Assert.Equal(0, runs); }
            if (runs == 1) scored++;

            BaseRunningEngine.Advance(OutcomeType.Single, null, new BaseRunner { Player = Runner(3) }, null,
                Runner(3), OffensiveStrategy.SwingAway, new Random(seed), out var plain);
            if (plain is not null) heldOut++;
        }

        // An average runner scores 55% of the time when sent, and is out on 60% of the rest: about 27%.
        Assert.InRange(sentOut / (double)n, 0.22, 0.32);
        Assert.InRange(scored / (double)n, 0.50, 0.60);
        Assert.Equal(0, heldOut);
    }

    [Fact]
    public void OnlyOneRunnerIsThrownOutOnAHit()
    {
        for (int seed = 0; seed < 2000; seed++)
        {
            var first = new BaseRunner { Player = Runner(1) };
            var second = new BaseRunner { Player = Runner(1) };
            var (f, s, t, runs) = BaseRunningEngine.Advance(OutcomeType.Single, first, second, null,
                Runner(3), OffensiveStrategy.SwingAgressive, new Random(seed), out var thrown);
            if (thrown is null) continue;

            // Whoever was thrown out, the other runner is still on a base or scored: never two outs on one hit.
            int accounted = new[] { f, s, t }.Count(r => r is not null && r.Player != thrown.Value.Runner.Player) + runs;
            Assert.Equal(2, accounted);   // the batter and the other runner
        }
    }

    [Fact]
    public void ARunnerThrownOutOnAHit_IsAnOutInTheResult_ATagInTheMoves_AndATagInThePicture()
    {
        var state = Game();
        var runner = state.LineupPlayers(true).Skip(4).First().Player;
        state.RunnerOnSecond = new BaseRunner { Player = runner };
        var offense = new OffensiveDecision { Strategy = OffensiveStrategy.SwingAgressive };

        PlayResult? play = null;
        for (int seed = 0; seed < 3000 && play is null; seed++)
        {
            var r = new AtBatResolver(new Random(seed)).Resolve(state, offense, new DefensiveDecision());
            if (r.ThrownOutFrom is not null) play = r;
        }

        Assert.NotNull(play);
        Assert.True(play!.IsHit);
        Assert.Equal(1, play.OutsRecorded);
        Assert.Equal(2, play.ThrownOutFrom);
        Assert.Equal(4, play.ThrownOutAt);
        Assert.Contains($"{runner.Name} is thrown out at the plate", play.Description);

        var moves = RunnerMoveBuilder.Build(state, play);
        var tagged = Assert.Single(moves, m => m.Player == runner);
        Assert.Equal((4, RunnerFate.TaggedOut), (tagged.ToBase, tagged.Fate));
        play.RunnerMoves = moves;

        var script = PlayChoreographer.Build(PrePlaySnapshot.Capture(state), play, seed: 3);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == "OUT AT THE PLATE!");
        var catcher = script.Tracks.Single(t => t.ActorId == "catcher").Keys;
        double tag = catcher.First(k => k.Pose == Poses.Tag).T;
        var ball = script.Tracks.Single(t => t.ActorId == "ball").Keys;
        double ballHome = ball.First(k => k.T > 1 && Math.Abs(k.X) < 6 && Math.Abs(k.Y) < 6).T;
        var runnerKeys = script.Tracks.Single(t => t.ActorId == $"runner-{runner.Id}").Keys;
        double runnerHome = runnerKeys.First(k => Math.Abs(k.X) < 1 && Math.Abs(k.Y) < 1).T;
        Assert.True(ballHome < runnerHome, $"ball {ballHome:0.00} must beat runner {runnerHome:0.00}");
        Assert.True(Math.Abs(tag - runnerHome) < 0.15 && tag > ballHome, $"tag {tag:0.00}, runner {runnerHome:0.00}, ball {ballHome:0.00}");
    }

    // ── Infield in ───────────────────────────────────────────────────────────

    [Fact]
    public void WithTheInfieldIn_TheRunnerOnThirdUsuallyHoldsOnAGroundOut()
    {
        int scoredIn = 0, scoredBack = 0, scoredForced = 0;
        const int n = 2000;
        for (int seed = 0; seed < n; seed++)
        {
            var third = new BaseRunner { Player = Runner(3) };
            var inR = BaseRunningEngine.GroundOut(null, null, third, DefensiveAlignment.InfieldIn, 1, new Random(seed));
            scoredIn += inR.runs;
            if (inR.runs == 0) Assert.Same(third, inR.third);

            scoredBack += BaseRunningEngine.GroundOut(null, null, third, DefensiveAlignment.Standard, 1, new Random(seed)).runs;

            // Bases loaded: the runner on third is forced and cannot hold.
            scoredForced += BaseRunningEngine.GroundOut(new BaseRunner { Player = Runner(3) },
                new BaseRunner { Player = Runner(3) }, third, DefensiveAlignment.InfieldIn, 1, new Random(seed)).runs;
        }

        Assert.InRange(scoredIn / (double)n, 0.20, 0.30);
        Assert.Equal(n, scoredBack);
        Assert.Equal(n, scoredForced);
    }

    [Fact]
    public void TheInfieldInCutsRunsWithARunnerOnThird_ButCostsHitsWithTheBasesEmpty()
    {
        double RunsAllowed(DefensiveAlignment alignment, bool runnerOnThird)
        {
            var state = Game();
            state.Outs = 1;
            state.HomeAlignment = alignment;
            double total = 0;
            for (int seed = 0; seed < 6000; seed++)
            {
                state.RunnerOnThird = runnerOnThird ? new BaseRunner { Player = Runner(3) } : null;
                var r = new AtBatResolver(new Random(seed)).Resolve(state,
                    new OffensiveDecision(), new DefensiveDecision { Alignment = alignment });
                int bases = (r.NewFirst is null ? 0 : 1) | (r.NewSecond is null ? 0 : 2) | (r.NewThird is null ? 0 : 4);
                total += r.RunsScored + WinProbability.ExpectedRuns(bases, Math.Min(2, state.Outs + r.OutsRecorded))
                    * (state.Outs + r.OutsRecorded >= 3 ? 0 : 1);
            }
            return total / 6000;
        }

        Assert.True(RunsAllowed(DefensiveAlignment.InfieldIn, true) < RunsAllowed(DefensiveAlignment.Standard, true));
        Assert.True(RunsAllowed(DefensiveAlignment.InfieldIn, false) > RunsAllowed(DefensiveAlignment.Standard, false));
    }

    // ── Every alignment against an ordinary hitter ───────────────────────────

    // The value of a plate appearance with the bases empty and nobody out, exactly, from the outcome weights.
    private static double BasesEmptyValue(OutcomeWeights w)
    {
        double total = w.HomeRun + w.Triple + w.Double + w.Single + w.Walk + w.HitByPitch + w.Strikeout + w.GroundOut + w.FlyOut;
        double re(int bases, int outs) => WinProbability.ExpectedRuns(bases, outs);
        return (w.HomeRun * (1 + re(0, 0)) + w.Triple * re(4, 0) + w.Double * re(2, 0)
            + (w.Single + w.Walk + w.HitByPitch) * re(1, 0)
            + (w.Strikeout + w.GroundOut + w.FlyOut) * re(0, 1)) / total;
    }

    public static TheoryData<string, int, int, int, int, int> OrdinaryHitters => new()
    {
        // name, AB, H, 2B, 3B, HR
        { "average", 550, 150, 28, 4, 12 },
        { "slugger", 550, 160, 32, 3, 38 },
        { "line-drive", 550, 175, 40, 6, 8 },
    };

    [Theory]
    [MemberData(nameof(OrdinaryHitters))]
    public void NoAlignmentBeatsNormalAgainstAnOrdinaryHitterWithTheBasesEmpty(string name, int ab, int h, int d2, int d3, int hr)
    {
        var batter = new BatterStats { AtBats = ab, Hits = h, Doubles = d2, Triples = d3, HomeRuns = hr, Walks = 55, Strikeouts = 80 };
        var pitcher = new PitcherStats { ERA = 3.6, InningsPitched = 220, HitsAllowed = 210, WalksAllowed = 65, Strikeouts = 140, GroundBallRate = 0.45 };
        double Value(DefensiveAlignment a) => BasesEmptyValue(ProbabilityTable.Build(batter, pitcher, Stadium.Generic,
            BatHand.Right, PitchType.Fastball, a, 1.0));

        double normal = Value(DefensiveAlignment.Standard);
        foreach (var alignment in Enum.GetValues<DefensiveAlignment>().Where(a => a != DefensiveAlignment.Standard))
            Assert.True(Value(alignment) >= normal - 1e-9,
                $"{alignment} beats Normal against the {name} hitter: {Value(alignment):0.0000} vs {normal:0.0000}");
    }

    // ── The pitchout ─────────────────────────────────────────────────────────

    [Fact]
    public void APitchoutToABatterWhoseRunnerStays_DoesNotSuppressHits()
    {
        var batter = new BatterStats { AtBats = 550, Hits = 150, Doubles = 28, Triples = 4, HomeRuns = 12, Walks = 55, Strikeouts = 80 };
        var pitcher = new PitcherStats { ERA = 3.6, InningsPitched = 220, HitsAllowed = 210, WalksAllowed = 65, Strikeouts = 140, GroundBallRate = 0.45 };
        OutcomeWeights W(PitchType p) => ProbabilityTable.Build(batter, pitcher, Stadium.Generic, BatHand.Right, p,
            DefensiveAlignment.Standard, 1.0);
        double Hits(OutcomeWeights w) => w.Single + w.Double + w.Triple + w.HomeRun;
        double Share(OutcomeWeights w, Func<OutcomeWeights, double> part) =>
            part(w) / (w.HomeRun + w.Triple + w.Double + w.Single + w.Walk + w.HitByPitch + w.Strikeout + w.GroundOut + w.FlyOut);

        var pitchout = W(PitchType.Pitchout);
        var ordinary = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }.Select(W).ToList();

        // Regression: it once cut hits to 10%, which made the pitchout the best pitch in the game.
        // WHY: the ordinary pitches trade hits for other things (they are balanced to the same run value), so compare with their
        // average: a pitchout that suppressed hits would sit far below it.
        Assert.True(Share(pitchout, Hits) >= 0.95 * ordinary.Average(w => Share(w, Hits)));
        Assert.True(Share(pitchout, w => w.Walk) > ordinary.Max(w => Share(w, x => x.Walk)));
        Assert.True(BasesEmptyValue(pitchout) > ordinary.Min(BasesEmptyValue),
            "a wasted pitch must cost the defense compared with its best ordinary pitch");
    }
}
