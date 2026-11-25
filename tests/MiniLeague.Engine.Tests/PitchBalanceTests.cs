using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// No pitch is a free option, and the data carries what the managers' habits are judged against. Calibration found that a manager
/// who picked the best pitch every time took about 8% off the other side's runs, because two pitches gave up fewer home runs and
/// singles for nothing.
/// </summary>
public class PitchBalanceTests
{
    private static string TeamsDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data", "teams"));

    // Linear weights, in runs against an out in play: what each result is worth.
    private static double RunValue(OutcomeWeights w) =>
        w.Single * 0.47 + w.Double * 0.77 + w.Triple * 1.04 + w.HomeRun * 1.40 + w.Walk * 0.33 + w.HitByPitch * 0.33 - w.Strikeout * 0.03;

    private static double ValuePerPlateAppearance(BatterStats batter, PitchType pitch)
    {
        // A pitcher exactly average for his league, so only the pitch changes anything.
        var pitcher = new PitcherStats { ERA = 4.0, InningsPitched = 180, HitsAllowed = 170, HomeRunsAllowed = 15, Strikeouts = 140, WalksAllowed = 60 };
        var w = ProbabilityTable.Build(batter, pitcher, Stadium.Generic, BatHand.Right, pitch, DefensiveAlignment.Standard, 1.0);
        double total = w.HomeRun + w.Triple + w.Double + w.Single + w.Walk + w.HitByPitch + w.Strikeout + w.GroundOut + w.FlyOut;
        return RunValue(w) / total;
    }

    [Fact]
    public void AgainstAnOrdinaryHitter_NoOrdinaryPitchBeatsAnother()
    {
        var hitter = new BatterStats { AtBats = 550, Hits = 160, Doubles = 30, Triples = 4, HomeRuns = 20, Walks = 60, Strikeouts = 80 };
        var values = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }
            .ToDictionary(p => p, p => ValuePerPlateAppearance(hitter, p));

        // The best pitch gains under a quarter of a run per game (a game is about 38 plate appearances) over the worst.
        Assert.InRange(values.Values.Max() - values.Values.Min(), 0, 0.0035);
    }

    [Fact]
    public void ThePitchesStillDiffer_ByWhatTheyTradeAgainstTheHitter()
    {
        var slugger = new BatterStats { AtBats = 500, Hits = 140, Doubles = 25, Triples = 2, HomeRuns = 40, Walks = 70, Strikeouts = 130 };
        var slap = new BatterStats { AtBats = 600, Hits = 190, Doubles = 20, Triples = 6, HomeRuns = 2, Walks = 40, Strikeouts = 40 };

        // A pitch that gives up fewer home runs suits the slugger, and one that gives up fewer singles the slap hitter.
        double sluggerSpread = Spread(slugger), slapSpread = Spread(slap);
        Assert.True(ValuePerPlateAppearance(slugger, PitchType.Changeup) < ValuePerPlateAppearance(slugger, PitchType.Fastball));
        Assert.True(ValuePerPlateAppearance(slap, PitchType.Fastball) < ValuePerPlateAppearance(slap, PitchType.Changeup));
        Assert.True(sluggerSpread > 0.002 && slapSpread > 0.002, $"{sluggerSpread} {slapSpread}");

        static double Spread(BatterStats b)
        {
            var v = new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup }.Select(p => ValuePerPlateAppearance(b, p)).ToList();
            return v.Max() - v.Min();
        }
    }

    [Fact]
    public void TheDoublePlayChanceIsTheOneTheCalibrationSet()
    {
        // An average runner against an average ground-ball pitcher turns two on 52% of ground outs with a runner on first.
        Assert.Equal(0.52, RunningRules.DoublePlayChanceFor(3, 0.45, runnerGoing: false), 9);
        Assert.Equal(RunningRules.DoublePlayChance, RunningRules.DoublePlayChanceFor(3, 0.45, false), 9);
    }

    [Fact]
    public void EveryShippedTeamCarriesItsSacrificeHits_ItsPitchersBattingLines_AndItsHabits()
    {
        foreach (var file in Directory.GetFiles(TeamsDir, "*.json"))
        {
            var team = TeamLoader.Parse(File.ReadAllText(file));
            var hitters = team.Roster.Where(p => !p.IsPitcher).ToList();

            Assert.NotNull(team.Tendencies);
            // some hitters bunt well and some never did: the ratings are no longer all the default 3
            Assert.True(hitters.Select(p => p.BuntingAbility).Distinct().Count() > 1, file);
            // a pitcher who batted has his own line, and a National League club's pitchers all did
            if (team.League == "NL" && team.Season < DesignatedHitterRules.NationalLeagueFrom)
                Assert.All(team.Roster.Where(p => p.IsPitcher && p.Role == PitcherRole.Starter), p => Assert.True(p.Overall!.AtBats >= 30, $"{file}: {p.Name}"));
        }
    }
}
