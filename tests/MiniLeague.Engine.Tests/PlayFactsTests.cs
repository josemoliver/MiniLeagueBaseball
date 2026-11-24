using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The per-runner moves, fielder and pre-play snapshot recorded on each play (used to show it).</summary>
public class PlayFactsTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(int seed)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)), new Random(seed));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"),
            ManagerControl.Computer, ManagerControl.Computer, options: GameOptions.None);
        return game;
    }

    /// <summary>Plays whole games, giving each play with the situation just before it.</summary>
    private static IEnumerable<(PlayResult Result, PrePlaySnapshot Before)> Plays(int games)
    {
        for (int seed = 1; seed <= games; seed++)
        {
            var game = NewGame(seed);
            int guard = 0;
            while (game.State!.Status == GameStatus.InProgress && guard++ < 2000)
            {
                var result = game.ExecuteComputerTurn();
                if (result is null) break;
                yield return (result, game.LastPrePlay!);
            }
        }
    }

    // ── The invariant: the moves always agree with the result ────────────────

    [Fact]
    public void Moves_AgreeWithBasesRunsAndOuts_OnEveryPlay()
    {
        int plays = 0;
        foreach (var (r, before) in Plays(40))
        {
            plays++;
            string what = $"{r.Outcome}: {r.Description}";

            Assert.Equal(r.RunsScored, r.RunnerMoves.Count(m => m.Fate == RunnerFate.Scored));
            Assert.Equal(r.OutsRecorded, r.RunnerMoves.Count(m => m.IsOut));

            // Everyone who was on a base, and the batter (except on a steal attempt), appears exactly once.
            var expected = new List<Player>();
            foreach (var b in new[] { before.First, before.Second, before.Third })
                if (b is not null) expected.Add(b.Player);
            if (r.Outcome is not (OutcomeType.StolenBase or OutcomeType.CaughtStealing)) expected.Add(before.Batter);
            Assert.True(expected.OrderBy(p => p.Id).SequenceEqual(r.RunnerMoves.Select(m => m.Player).OrderBy(p => p.Id)), what);

            // Where each started.
            foreach (var m in r.RunnerMoves)
            {
                int from = ReferenceEquals(m.Player, before.First?.Player) ? 1
                         : ReferenceEquals(m.Player, before.Second?.Player) ? 2
                         : ReferenceEquals(m.Player, before.Third?.Player) ? 3 : 0;
                Assert.True(from == m.FromBase, what);
            }

            // Unless the inning ended, applying the moves gives exactly the new base state.
            if (before.Outs + r.OutsRecorded < 3)
            {
                var bases = new Player?[4];
                foreach (var m in r.RunnerMoves.Where(m => m.Fate is RunnerFate.Safe or RunnerFate.StayedPut))
                {
                    Assert.True(m.ToBase is >= 1 and <= 3, what);
                    Assert.Null(bases[m.ToBase]);
                    bases[m.ToBase] = m.Player;
                }
                Assert.Same(r.NewFirst?.Player, bases[1]);
                Assert.Same(r.NewSecond?.Player, bases[2]);
                Assert.Same(r.NewThird?.Player, bases[3]);
            }
        }
        Assert.True(plays > 1500);
    }

    [Fact]
    public void EveryKindOfPlayTheEngineProduces_IsCoveredByTheInvariantTest()
    {
        var seen = Plays(40).Select(p => p.Result.Outcome).ToHashSet();
        foreach (var o in new[]
        {
            OutcomeType.Single, OutcomeType.Double, OutcomeType.HomeRun, OutcomeType.Walk, OutcomeType.Strikeout,
            OutcomeType.GroundOut, OutcomeType.FlyOut, OutcomeType.DoublePlay, OutcomeType.Error,
            OutcomeType.StolenBase, OutcomeType.CaughtStealing,
        })
            Assert.Contains(o, seen);
    }

    // ── Specific plays ───────────────────────────────────────────────────────

    private static (PlayResult Result, PrePlaySnapshot Before) First(Func<PlayResult, PrePlaySnapshot, bool> match) =>
        Plays(60).First(p => match(p.Result, p.Before));

    [Fact]
    public void DoublePlay_RetiresTheBatterAtFirstAndTheRunnerFromFirstAtSecond()
    {
        var (r, before) = First((r, _) => r.Outcome == OutcomeType.DoublePlay);

        var batter = r.RunnerMoves.Single(m => m.FromBase == 0);
        Assert.Equal((1, RunnerFate.ForcedOut), (batter.ToBase, batter.Fate));
        Assert.Same(before.Batter, batter.Player);

        var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
        Assert.Equal((2, RunnerFate.ForcedOut), (runner.ToBase, runner.Fate));
        Assert.Equal(2, r.RunnerMoves.Count(m => m.IsOut));
    }

    [Fact]
    public void CaughtStealing_TagsTheRunnerOutAtSecondAndHasNoBatter()
    {
        var (r, before) = First((r, _) => r.Outcome == OutcomeType.CaughtStealing);

        var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
        Assert.Equal((2, RunnerFate.TaggedOut), (runner.ToBase, runner.Fate));
        Assert.DoesNotContain(r.RunnerMoves, m => ReferenceEquals(m.Player, before.Batter));
    }

    [Fact]
    public void StolenBase_MovesTheRunnerToSecond()
    {
        var (r, _) = First((r, _) => r.Outcome == OutcomeType.StolenBase);

        var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
        Assert.Equal((2, RunnerFate.Safe), (runner.ToBase, runner.Fate));
        Assert.DoesNotContain(r.RunnerMoves, m => m.IsOut);
    }

    [Fact]
    public void HomeRun_ScoresEveryone_BatterIncluded()
    {
        var (r, before) = First((r, b) => r.Outcome == OutcomeType.HomeRun && b.First is not null);

        Assert.All(r.RunnerMoves, m => Assert.Equal((4, RunnerFate.Scored), (m.ToBase, m.Fate)));
        Assert.Contains(r.RunnerMoves, m => ReferenceEquals(m.Player, before.Batter));
    }

    [Fact]
    public void Strikeout_RetiresTheBatterAndLeavesRunnersWhereTheyAre()
    {
        var (r, _) = First((r, b) => r.Outcome == OutcomeType.Strikeout && b.Second is not null);

        var batter = r.RunnerMoves.Single(m => m.FromBase == 0);
        Assert.Equal((0, RunnerFate.Retired), (batter.ToBase, batter.Fate));
        var runner = r.RunnerMoves.Single(m => m.FromBase == 2);
        Assert.Equal((2, RunnerFate.StayedPut), (runner.ToBase, runner.Fate));
    }

    [Fact]
    public void SacrificeFly_ScoresTheRunnerFromThirdAfterTheCatch()
    {
        var (r, _) = First((r, _) => r.Outcome == OutcomeType.FlyOut && r.RunsScored > 0);

        Assert.Equal(RunnerFate.Scored, r.RunnerMoves.Single(m => m.FromBase == 3).Fate);
        Assert.Equal(RunnerFate.Retired, r.RunnerMoves.Single(m => m.FromBase == 0).Fate);
    }

    // ── Fielder ──────────────────────────────────────────────────────────────

    [Fact]
    public void Fielder_IsRecordedOnGroundOutsFlyOutsDoublePlaysAndErrors_AndNowhereElse()
    {
        foreach (var (r, _) in Plays(40))
        {
            // A bunt always goes to somebody, and so does a line drive that doubles off a hit-and-run runner.
            bool shouldHave = r.Outcome is OutcomeType.GroundOut or OutcomeType.FlyOut
                or OutcomeType.DoublePlay or OutcomeType.Error
                || r.IsBunt
                || r is { Outcome: OutcomeType.LineOut, HitAndRun: true };
            Assert.Equal(shouldHave, r.Fielder is not null);
            if (r.Outcome == OutcomeType.Error) Assert.Equal(r.ErrorPosition, r.Fielder);
        }
    }

    [Fact]
    public void Fielder_OnAGroundOutIsAGroundBallFielder_AndOnAFlyOutMostOftenAnOutfielder()
    {
        var plays = Plays(40).Select(p => p.Result).ToList();
        Assert.All(plays.Where(r => r.Outcome == OutcomeType.GroundOut),
            r => Assert.Contains(r.Fielder!.Value,
                new[] { Position.Short, Position.Second, Position.Third, Position.First, Position.P, Position.C }));

        var flies = plays.Where(r => r.Outcome == OutcomeType.FlyOut).ToList();
        int outfield = flies.Count(r => r.Fielder is Position.Left or Position.Center or Position.Right);
        Assert.True(outfield > flies.Count / 2);
    }

    // ── Pre-play snapshot ────────────────────────────────────────────────────

    [Fact]
    public void LastPrePlay_IsNullBeforeAPlay_AndDescribesTheSituationBeforeTheNextOne()
    {
        var game = NewGame(3);
        Assert.Null(game.LastPrePlay);

        var s = game.State!;
        var batter = s.CurrentBatter;
        var pitcher = s.CurrentPitcher.Player;
        game.ExecuteComputerTurn();

        var snap = game.LastPrePlay!;
        Assert.Same(batter, snap.Batter);
        Assert.Same(pitcher, snap.Pitcher);
        Assert.Equal(1, snap.Inning);
        Assert.True(snap.IsTop);
        Assert.Equal(0, snap.Outs);
        Assert.Null(snap.First);
        Assert.Equal(8, snap.Fielders.Count);
        Assert.Same(s.Stadium, snap.Stadium);
    }

    [Fact]
    public void LastPrePlay_StillShowsTheEarlierSituation_AfterThePlayHasBeenApplied()
    {
        foreach (var (r, before) in Plays(2))
        {
            var scorer = r.RunnerMoves.FirstOrDefault(m => m.Fate == RunnerFate.Scored);
            if (scorer is null) continue;

            // The scorer was on a base (or at bat) before the play, which the snapshot still shows.
            var was = scorer.FromBase switch
            {
                0 => before.Batter,
                1 => before.First?.Player,
                2 => before.Second?.Player,
                _ => before.Third?.Player,
            };
            Assert.Same(scorer.Player, was);
            return;
        }
        Assert.Fail("no run scored in two games");
    }

    [Fact]
    public void LastPrePlay_IsClearedWhenAGameIsResumed()
    {
        var game = NewGame(4);
        game.ExecuteComputerTurn();
        Assert.NotNull(game.LastPrePlay);

        var snapshot = GameSnapshots.Capture(game.State!);
        var teams = new[] { game.State!.VisitorTeam, game.State.HomeTeam };
        game.Resume(snapshot, id => teams.FirstOrDefault(t => t.Id == id));
        Assert.Null(game.LastPrePlay);
    }

    [Fact]
    public void BatsAgainst_SwitchHittersTakeTheSideOppositeThePitchersArm()
    {
        var sw = new Player { BatHand = BatHand.Switch };
        Assert.Equal(BatHand.Right, sw.BatsAgainst(ThrowHand.Left));
        Assert.Equal(BatHand.Left, sw.BatsAgainst(ThrowHand.Right));
        Assert.Equal(BatHand.Left, new Player { BatHand = BatHand.Left }.BatsAgainst(ThrowHand.Left));
    }
}
