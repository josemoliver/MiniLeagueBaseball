using System.Text.Json;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>
/// A pitcher is scaled against his own league and season, not against fixed numbers: an average pitcher of any era leaves
/// a batter's line alone, and the hits and home runs a pitcher allows are scaled by their own rates.
/// </summary>
public class LeaguePitchingTests
{
    private static readonly BatterStats Hitter = new()
    { AtBats = 550, Hits = 160, Doubles = 30, Triples = 4, HomeRuns = 20, Walks = 60, Strikeouts = 80 };

    private static string TeamsDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data", "teams"));

    /// <summary>A pitcher of 180 innings with exactly this league's rates (nine-inning blocks: 20 of them).</summary>
    private static PitcherStats AveragePitcher(LeaguePitching league) => new()
    {
        League = league, ERA = league.Era, InningsPitched = 180,
        HitsAllowed = (int)Math.Round((league.NonHomeRunHitsPerNine + league.HomeRunsPerNine) * 20),
        HomeRunsAllowed = (int)Math.Round(league.HomeRunsPerNine * 20),
        Strikeouts = (int)Math.Round(league.StrikeoutsPerNine * 20),
        WalksAllowed = (int)Math.Round(league.WalksPerNine * 20),
    };

    private static OutcomeWeights Weights(PitcherStats pitcher) => ProbabilityTable.Build(Hitter, pitcher, Stadium.Generic,
        BatHand.Right, PitchType.Fastball, DefensiveAlignment.Standard, 1.0);

    // What the engine did before league figures: a pitcher with a 4.00 ERA, 7.0 strikeouts and 3.0 walks per nine.
    private static readonly PitcherStats Reference = new()
    { ERA = 4.0, InningsPitched = 180, HitsAllowed = 170, HomeRunsAllowed = 15, Strikeouts = 140, WalksAllowed = 60 };

    [Theory]
    [InlineData(2.98, 6.96, 0.68, 5.96, 3.02)]   // 1968 American League: a pitchers' year
    [InlineData(4.14, 9.50, 0.36, 2.78, 3.30)]   // 1927 American League: few strikeouts, many hits
    [InlineData(4.07, 8.18, 0.92, 4.83, 3.25)]   // 1982 American League
    public void AnAveragePitcherOfAnyEra_LeavesTheBattersLineAlone(double era, double hits, double homeRuns, double strikeouts, double walks)
    {
        var league = new LeaguePitching(era, hits, homeRuns, strikeouts, walks);
        var w = Weights(AveragePitcher(league));
        var baseline = Weights(Reference);

        // WHY: the baseline pitcher is neutral under the old fixed references, so an average pitcher must match him.
        Near(baseline.Single, w.Single);
        Near(baseline.Double, w.Double);
        Near(baseline.HomeRun, w.HomeRun);
        Near(baseline.Walk, w.Walk);
        Near(baseline.Strikeout, w.Strikeout);
    }

    [Fact]
    public void AGoodPitcherAllowsFewerHitsThanAnAverageOne_ButNotInProportionToHisEra()
    {
        var league = new LeaguePitching(3.6, 8.0, 0.8, 5.0, 3.2);
        var average = AveragePitcher(league);
        var good = AveragePitcher(league);
        good.ERA = league.Era * 0.8;                        // a fifth fewer earned runs
        good.HitsAllowed = (int)(average.HitsAllowed * 0.93);   // but only 7% fewer hits, as a real staff does

        double averageHits = Hits(Weights(average)), goodHits = Hits(Weights(good));

        Assert.True(goodHits < averageHits);
        Assert.True(goodHits > averageHits * 0.85, $"{goodHits} vs {averageHits}: hits must not follow ERA one for one");
    }

    [Fact]
    public void WithLeagueFigures_HitsFollowTheHitRatesNotTheEra()
    {
        var league = new LeaguePitching(3.6, 8.0, 0.8, 5.0, 3.2);
        var a = AveragePitcher(league);
        var b = AveragePitcher(league);
        b.ERA = 1.5;   // a far better ERA, with the same hits and home runs allowed

        Assert.Equal(Hits(Weights(a)), Hits(Weights(b)), 9);
    }

    [Fact]
    public void WithoutLeagueFigures_TheEraStillScalesHits()
    {
        var plain = new PitcherStats { ERA = 4.0, InningsPitched = 180, HitsAllowed = 170, Strikeouts = 140, WalksAllowed = 60 };
        var good = new PitcherStats { ERA = 2.0, InningsPitched = 180, HitsAllowed = 170, Strikeouts = 140, WalksAllowed = 60 };

        Assert.Equal(LeaguePitching.Default, plain.League);
        Assert.Equal(0.5, Hits(Weights(good)) / Hits(Weights(plain)), 2);
    }

    [Fact]
    public void ATiredPitcherAllowsMoreHits()
    {
        var p = AveragePitcher(new LeaguePitching(3.6, 8.0, 0.8, 5.0, 3.2));
        var fresh = Weights(p);
        var tired = ProbabilityTable.Build(Hitter, p, Stadium.Generic, BatHand.Right, PitchType.Fastball,
            DefensiveAlignment.Standard, 0.8);

        Assert.True(Hits(tired) > Hits(fresh));
    }

    // WHY: the pitcher's counts are whole numbers (7 home runs for a rate of 7.2), so "the same" means within a few percent.
    private static void Near(double expected, double actual) => Assert.InRange(actual / expected, 0.97, 1.03);

    private static double Hits(OutcomeWeights w) => w.Single + w.Double + w.Triple + w.HomeRun;

    // ── The data ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryShippedTeamCarriesItsLeaguesPitching_AndItsPitchersAreScaledByIt()
    {
        var files = Directory.GetFiles(TeamsDir, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var team = TeamLoader.Parse(File.ReadAllText(file));
            var lp = team.LeaguePitching;

            Assert.NotEqual(LeaguePitching.Default, lp);
            Assert.True(lp.HasHitRates, file);
            Assert.InRange(lp.Era, 2.5, 4.5);
            Assert.InRange(lp.StrikeoutsPerNine, 2.0, 7.0);
            Assert.InRange(lp.WalksPerNine, 2.0, 4.5);
            Assert.All(team.Roster.Where(p => p.IsPitcher), p => Assert.Same(lp, p.PitchingOverall!.League));
        }
    }

    [Fact]
    public void ATeamFileWithoutLeagueFigures_StillLoads_WithTheOldReferences()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(TeamsDir, "*.json").First()));
        var root = doc.RootElement;
        var withoutLeague = JsonSerializer.Serialize(
            root.EnumerateObject().Where(p => p.Name != "leaguePitching").ToDictionary(p => p.Name, p => p.Value));

        var team = TeamLoader.Parse(withoutLeague);

        Assert.Equal(LeaguePitching.Default, team.LeaguePitching);
        Assert.All(team.Roster.Where(p => p.IsPitcher), p => Assert.Equal(LeaguePitching.Default, p.PitchingOverall!.League));
    }
}
