using MiniLeague.Engine.Models;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

public class AtBatResolverTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Player MakeBatter(string name, int ab, int h, int hr, int bb, int so,
                                      int d2 = 0, int d3 = 0, int speed = 3)
    {
        var stats = new BatterStats
        {
            AtBats = ab, Hits = h, HomeRuns = hr, Walks = bb,
            Strikeouts = so, Doubles = d2, Triples = d3
        };
        return new Player
        {
            Id = 1, Name = name, BatHand = BatHand.Right,
            ThrowHand = ThrowHand.Right, PrimaryPosition = Position.Left,
            SpeedRating = speed,
            Overall = stats, VsLeft = stats, VsRight = stats
        };
    }

    private static Player MakePitcher(string name, double era, int ip, int h, int bb, int so)
    {
        var stats = new PitcherStats
        {
            ERA = era, InningsPitched = ip, HitsAllowed = h,
            WalksAllowed = bb, Strikeouts = so, Wins = 15, Losses = 8,
            GroundBallRate = 0.45, TiringFactor = 5
        };
        return new Player
        {
            Id = 99, Name = name, ThrowHand = ThrowHand.Right,
            PrimaryPosition = Position.P, Role = PitcherRole.Starter,
            PitchingOverall = stats, PitchingVsLeft = stats, PitchingVsRight = stats
        };
    }

    private static GameState MakeGameState(Player batter, Player pitcher)
    {
        var activePitcher = new ActivePitcher { Player = pitcher, PitchCount = 50 };

        return new GameState
        {
            VisitorTeam = new Team
            {
                Id = 1, City = "New York", Nickname = "Yankees",
                Abbreviation = "NYY", Season = 1927,
                Roster = [batter],
            },
            HomeTeam = new Team
            {
                Id = 2, City = "Cincinnati", Nickname = "Reds",
                Abbreviation = "CIN", Season = 1975,
                Roster = [pitcher],
            },
            Stadium = Stadium.Generic,
            IsTopOfInning = true,
            CurrentInning = 1,
            Outs = 0,
            VisitorLineup = [batter.Id],
            HomeLineup = [pitcher.Id],
            VisitorPitcher = activePitcher,
            HomePitcher = activePitcher,
            Status = GameStatus.InProgress,
            VisitorControl = ManagerControl.Human,
            HomeControl = ManagerControl.Computer,
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IntentionalWalk_AlwaysProducesWalk()
    {
        var batter  = MakeBatter("B.Ruth", 540, 192, 60, 138, 89, 29, 8, 6);
        var pitcher = MakePitcher("W.Hoyt", 2.64, 256, 242, 86, 86);
        var state   = MakeGameState(batter, pitcher);
        var resolver = new AtBatResolver(new Random(42));

        var result = resolver.Resolve(state,
            new OffensiveDecision { Strategy = OffensiveStrategy.SwingAway },
            new DefensiveDecision { IntentionalWalk = true });

        Assert.Equal(OutcomeType.IntentionalWalk, result.Outcome);
        Assert.Equal(0, result.OutsRecorded);
        Assert.NotNull(result.NewFirst);
    }

    [Fact]
    public void HomeRun_ScoresBatterAndAllRunners()
    {
        // Force a HR by seeding the RNG to roll within the HR bucket
        // Ruth: HR/PA ≈ 60/700 ≈ 8.6% — use a weak pitcher to inflate
        var batter  = MakeBatter("B.Ruth", 540, 192, 60, 138, 89, 29, 8, 6);
        var pitcher = MakePitcher("Weak.P", 6.50, 100, 140, 60, 40);  // bad pitcher inflates HR rate
        var state   = MakeGameState(batter, pitcher);

        // Place runners on all bases
        var runner = new BaseRunner { Player = batter, BattingSlot = 0 };
        state.RunnerOnFirst  = runner;
        state.RunnerOnSecond = runner;
        state.RunnerOnThird  = runner;

        var resolver = new AtBatResolver(new Random(42));

        // Run 200 at-bats; at least one should be a HR and score 4 runs
        bool foundHR = false;
        for (int i = 0; i < 200; i++)
        {
            var r = resolver.Resolve(state,
                new OffensiveDecision { Strategy = OffensiveStrategy.SwingAway },
                new DefensiveDecision { PitchType = PitchType.Fastball });
            if (r.Outcome == OutcomeType.HomeRun)
            {
                Assert.Equal(4, r.RunsScored);
                Assert.Null(r.NewFirst);
                Assert.Null(r.NewSecond);
                Assert.Null(r.NewThird);
                foundHR = true;
                break;
            }
        }
        Assert.True(foundHR, "Expected at least one HR in 200 at-bats against a weak pitcher");
    }

    [Fact]
    public void Walk_ForcesRunnerHome_BasesLoaded()
    {
        var batter  = MakeBatter("P.Collins", 251, 69, 7, 54, 30, 10, 2, 5);
        var pitcher = MakePitcher("Wild.P", 5.00, 120, 130, 90, 60);
        var state   = MakeGameState(batter, pitcher);

        var runner = new BaseRunner { Player = batter, BattingSlot = 0 };
        state.RunnerOnFirst  = runner;
        state.RunnerOnSecond = runner;
        state.RunnerOnThird  = runner;

        var resolver = new AtBatResolver(new Random(42));

        bool foundWalk = false;
        for (int i = 0; i < 200; i++)
        {
            var r = resolver.Resolve(state,
                new OffensiveDecision { Strategy = OffensiveStrategy.SwingAway },
                new DefensiveDecision { PitchType = PitchType.Fastball });
            if (r.Outcome == OutcomeType.Walk)
            {
                Assert.Equal(1, r.RunsScored); // runner from third forced home
                foundWalk = true;
                break;
            }
        }
        Assert.True(foundWalk, "Expected at least one walk in 200 at-bats");
    }

    [Fact]
    public void StrongerPitcher_ProducesLowerHitRate()
    {
        var batter    = MakeBatter("B.Ruth", 540, 192, 60, 138, 89, 29, 8, 6);
        var goodP     = MakePitcher("Good.P",  2.00, 250, 180, 50,  250);
        var badP      = MakePitcher("Bad.P",   6.00, 100, 160, 80,   40);
        var stateGood = MakeGameState(batter, goodP);
        var stateBad  = MakeGameState(batter, badP);

        var rng = new Random(1337);
        var resolverGood = new AtBatResolver(rng);
        var resolverBad  = new AtBatResolver(new Random(1337));

        int goodHits = 0, badHits = 0;
        const int n = 1000;
        var offDec  = new OffensiveDecision { Strategy = OffensiveStrategy.SwingAway };
        var defDec  = new DefensiveDecision { PitchType = PitchType.Fastball };

        for (int i = 0; i < n; i++)
        {
            if (resolverGood.Resolve(stateGood, offDec, defDec).IsHit) goodHits++;
            if (resolverBad.Resolve(stateBad, offDec, defDec).IsHit)  badHits++;
        }

        // Good pitcher should allow materially fewer hits
        Assert.True(goodHits < badHits,
            $"Expected fewer hits vs good pitcher. Good={goodHits}, Bad={badHits}");
    }

    [Fact]
    public void ProbabilityTable_WeightsArePositive()
    {
        var batter  = new BatterStats { AtBats = 540, Hits = 192, HomeRuns = 60, Walks = 138, Strikeouts = 89, Doubles = 29, Triples = 8 };
        var pitcher = new PitcherStats { ERA = 2.64, InningsPitched = 256, HitsAllowed = 242, WalksAllowed = 86, Strikeouts = 86, GroundBallRate = 0.45 };

        var weights = ProbabilityTable.Build(batter, pitcher, Stadium.Generic,
            BatHand.Left, PitchType.Fastball, DefensiveAlignment.Standard, 1.0);

        Assert.True(weights.HomeRun  >= 0);
        Assert.True(weights.Single   >= 0);
        Assert.True(weights.Walk     >= 0);
        Assert.True(weights.Strikeout >= 0);
        Assert.True(weights.GroundOut >= 0);
        Assert.True(weights.FlyOut   >= 0);
    }

    [Fact]
    public void FatigueModel_ReducesModifierAbove100Pitches()
    {
        var pitcher = MakePitcher("Tired.P", 3.00, 200, 180, 70, 150);
        var fresh   = new ActivePitcher { Player = pitcher, PitchCount = 50 };
        var tired   = new ActivePitcher { Player = pitcher, PitchCount = 120 };

        double freshMod = FatigueModel.GetModifier(fresh);
        double tiredMod = FatigueModel.GetModifier(tired);

        Assert.Equal(1.0, freshMod);
        Assert.True(tiredMod < freshMod, $"Expected tired mod {tiredMod} < fresh {freshMod}");
    }
}
