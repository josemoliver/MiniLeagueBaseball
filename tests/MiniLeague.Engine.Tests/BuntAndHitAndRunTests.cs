using MiniLeague.Engine.AI;
using MiniLeague.Engine.Animation;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The Sacrifice (bunt) and Hit &amp; Run calls: what they do to the result, the box score and the picture.</summary>
public class BuntAndHitAndRunTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static readonly OffensiveDecision Bunt = new() { Strategy = OffensiveStrategy.SacrificeBunt };
    private static readonly OffensiveDecision HitAndRun = new() { Strategy = OffensiveStrategy.HitAndRun };
    private static readonly OffensiveDecision Swing = new() { Strategy = OffensiveStrategy.SwingAway };

    /// <summary>The start of a visitor at-bat with the given runners and outs (the runners are other players on the roster).</summary>
    private static GameState Situation(bool first, bool second, bool third, int outs, int? batterSpeed = null)
    {
        var s = NewGame(1).State!;
        if (batterSpeed is int speed) s.CurrentBatter.SpeedRating = speed;
        var others = s.VisitorTeam.Roster.Where(p => !p.IsPitcher && p.Id != s.CurrentBatter.Id).ToList();
        s.RunnerOnFirst = first ? new BaseRunner { Player = others[0] } : null;
        s.RunnerOnSecond = second ? new BaseRunner { Player = others[1] } : null;
        s.RunnerOnThird = third ? new BaseRunner { Player = others[2] } : null;
        s.Outs = outs;
        return s;
    }

    private static List<PlayResult> Resolve(GameState s, OffensiveDecision offense, int n, DefensiveDecision? defense = null, int seed = 11)
    {
        var resolver = new AtBatResolver(new Random(seed));
        var results = new List<PlayResult>(n);
        for (int i = 0; i < n; i++)
            results.Add(resolver.Resolve(s, offense, defense ?? new DefensiveDecision()));
        return results;
    }

    private static double Rate(IEnumerable<PlayResult> results, Func<PlayResult, bool> match)
    {
        var list = results.ToList();
        return (double)list.Count(match) / list.Count;
    }

    // ── A bunt with a runner on: a sacrifice ─────────────────────────────────

    [Fact]
    public void Bunt_WithARunnerOnFirstAndNobodyOut_IsMostOftenASacrifice_AndEndsOneOfFiveWays()
    {
        var results = Resolve(Situation(true, false, false, 0), Bunt, 4000);

        Assert.All(results, r =>
        {
            Assert.True(r.IsBunt);
            Assert.Contains(r.Outcome, new[]
            {
                OutcomeType.SacrificeBunt, OutcomeType.FieldersChoice, OutcomeType.Single,
                OutcomeType.PopOut, OutcomeType.Error,
            });
        });
        Assert.True(Rate(results, r => r.Outcome == OutcomeType.SacrificeBunt) > 0.5);
        foreach (var kind in new[] { OutcomeType.FieldersChoice, OutcomeType.Single, OutcomeType.PopOut, OutcomeType.Error })
            Assert.Contains(results, r => r.Outcome == kind);
    }

    [Fact]
    public void ASacrifice_ThrowsOutTheBatterAndMovesEveryRunnerUpOneBase()
    {
        var s = Situation(true, true, false, 0);
        var runnerOnFirst = s.RunnerOnFirst!.Player;
        var runnerOnSecond = s.RunnerOnSecond!.Player;
        var sac = Resolve(s, Bunt, 400).First(r => r.Outcome == OutcomeType.SacrificeBunt);

        Assert.Equal(1, sac.OutsRecorded);
        Assert.Null(sac.NewFirst);
        Assert.Same(runnerOnFirst, sac.NewSecond!.Player);
        Assert.Same(runnerOnSecond, sac.NewThird!.Player);
        Assert.Equal(0, sac.RunsScored);

        var batter = sac.RunnerMoves.Single(m => m.FromBase == 0);
        Assert.Equal((1, RunnerFate.ForcedOut), (batter.ToBase, batter.Fate));
        Assert.Equal(2, sac.RunnerMoves.Count(m => m.Fate == RunnerFate.Safe));
        Assert.Contains("sacrifice", sac.Description);
    }

    [Fact]
    public void ASacrificeWithARunnerOnThird_ScoresHim()
    {
        var sac = Resolve(Situation(false, false, true, 0), Bunt, 400).First(r => r.Outcome == OutcomeType.SacrificeBunt);

        Assert.Equal(1, sac.RunsScored);
        Assert.Null(sac.NewThird);
        Assert.Equal(1, sac.RunnerMoves.Count(m => m.Fate == RunnerFate.Scored));
    }

    [Fact]
    public void AFieldersChoice_RetiresTheLeadRunnerAndPutsTheBatterOnFirst()
    {
        var s = Situation(true, false, false, 0);
        var runner = s.RunnerOnFirst!.Player;
        var choice = Resolve(s, Bunt, 1000).First(r => r.Outcome == OutcomeType.FieldersChoice);

        Assert.Equal(1, choice.OutsRecorded);
        Assert.Same(s.CurrentBatter, choice.NewFirst!.Player);
        Assert.Null(choice.NewSecond);
        var out_ = choice.RunnerMoves.Single(m => m.Player == runner);
        Assert.Equal((2, RunnerFate.ForcedOut), (out_.ToBase, out_.Fate));
        var batter = choice.RunnerMoves.Single(m => m.FromBase == 0);
        Assert.Equal((1, RunnerFate.Safe), (batter.ToBase, batter.Fate));
        Assert.Contains("fielder's choice", choice.Description);
    }

    [Fact]
    public void AFieldersChoiceWithARunnerOnSecondAlone_IsATagAtThird()
    {
        var choice = Resolve(Situation(false, true, false, 0), Bunt, 1000).First(r => r.Outcome == OutcomeType.FieldersChoice);

        var runner = choice.RunnerMoves.Single(m => m.FromBase == 2);
        Assert.Equal((3, RunnerFate.TaggedOut), (runner.ToBase, runner.Fate));   // not forced: nobody was behind him
    }

    [Fact]
    public void WithTheBasesLoaded_AFieldersChoiceTakesTheRunnerAtThird_AndTheRunnerFromThirdScores()
    {
        var choice = Resolve(Situation(true, true, true, 0), Bunt, 1000).First(r => r.Outcome == OutcomeType.FieldersChoice);

        Assert.Equal(1, choice.RunsScored);
        Assert.Equal(RunnerFate.ForcedOut, choice.RunnerMoves.Single(m => m.FromBase == 2).Fate);
        Assert.Equal(RunnerFate.Scored, choice.RunnerMoves.Single(m => m.FromBase == 3).Fate);
    }

    // ── A bunt with nobody to move: a bunt for a hit ─────────────────────────

    [Theory]
    [InlineData(false, false, false, 0)]   // bases empty
    [InlineData(false, true, false, 2)]    // two out: giving himself up would end the inning
    [InlineData(true, true, true, 2)]
    public void WithNobodyToMoveOrTwoOut_ABuntIsForAHit_NeverASacrificeOrAFieldersChoice(bool first, bool second, bool third, int outs)
    {
        var results = Resolve(Situation(first, second, third, outs), Bunt, 3000);

        Assert.DoesNotContain(results, r => r.Outcome is OutcomeType.SacrificeBunt or OutcomeType.FieldersChoice);
        Assert.Contains(results, r => r.Outcome == OutcomeType.GroundOut);
        Assert.Contains(results, r => r.Outcome == OutcomeType.Single);
        Assert.True(Rate(results, r => r.Outcome == OutcomeType.GroundOut) > 0.6);   // mostly just thrown out
    }

    [Fact]
    public void ABuntThrownOutWithTwoOut_LeavesTheRunnersWhereTheyWere()
    {
        var s = Situation(false, true, false, 2);
        var runner = s.RunnerOnSecond!.Player;
        var thrownOut = Resolve(s, Bunt, 400).First(r => r.Outcome == OutcomeType.GroundOut);

        Assert.Equal(1, thrownOut.OutsRecorded);
        Assert.Same(runner, thrownOut.NewSecond!.Player);
        Assert.Equal(0, thrownOut.RunsScored);
    }

    [Fact]
    public void ABuntSingle_MovesEveryRunnerUpOneBase()
    {
        var s = Situation(true, false, true, 2);
        var single = Resolve(s, Bunt, 1500).First(r => r.Outcome == OutcomeType.Single);

        Assert.Equal(1, single.RunsScored);                        // the runner on third scores
        Assert.Same(s.CurrentBatter, single.NewFirst!.Player);
        Assert.Same(s.RunnerOnFirst!.Player, single.NewSecond!.Player);
        Assert.Null(single.NewThird);
    }

    // ── What shapes a bunt ───────────────────────────────────────────────────

    [Fact]
    public void CornersIn_TakesAwayBuntHitsAndGetsMoreLeadRunners()
    {
        var s = Situation(true, false, false, 0);
        var standard = Resolve(s, Bunt, 6000, new DefensiveDecision { Alignment = DefensiveAlignment.Standard });
        var corners = Resolve(s, Bunt, 6000, new DefensiveDecision { Alignment = DefensiveAlignment.CornersIn });

        Assert.True(Rate(corners, r => r.Outcome == OutcomeType.Single) < Rate(standard, r => r.Outcome == OutcomeType.Single));
        Assert.True(Rate(corners, r => r.Outcome == OutcomeType.FieldersChoice) > Rate(standard, r => r.Outcome == OutcomeType.FieldersChoice));
        Assert.True(Rate(corners, r => r.Outcome == OutcomeType.SacrificeBunt) < Rate(standard, r => r.Outcome == OutcomeType.SacrificeBunt));
    }

    [Fact]
    public void AFastBatter_BeatsOutMoreBuntsThanASlowOne()
    {
        var fast = Resolve(Situation(false, false, false, 0, batterSpeed: 5), Bunt, 5000);
        var slow = Resolve(Situation(false, false, false, 0, batterSpeed: 1), Bunt, 5000);

        Assert.True(Rate(fast, r => r.Outcome == OutcomeType.Single) > 2 * Rate(slow, r => r.Outcome == OutcomeType.Single));
    }

    [Fact]
    public void EveryBunt_HasMovesThatAgreeWithTheResult()
    {
        foreach (var (first, second, third, outs) in new[]
        {
            (true, false, false, 0), (false, true, false, 0), (false, false, true, 1), (true, true, false, 1),
            (true, true, true, 0), (false, true, true, 0), (false, false, false, 0), (true, false, true, 2),
        })
        {
            var s = Situation(first, second, third, outs);
            foreach (var r in Resolve(s, Bunt, 600))
            {
                string what = $"{r.Outcome} {r.Description} (on {first}/{second}/{third}, {outs} out)";
                Assert.Equal(r.RunsScored, r.RunnerMoves.Count(m => m.Fate == RunnerFate.Scored));
                Assert.Equal(r.OutsRecorded, r.RunnerMoves.Count(m => m.IsOut));
                Assert.Equal(1 + (first ? 1 : 0) + (second ? 1 : 0) + (third ? 1 : 0), r.RunnerMoves.Count);
                Assert.NotNull(r.Fielder);
                // nobody shares a base, and a run never scores on the third out
                var bases = new[] { r.NewFirst, r.NewSecond, r.NewThird }.Where(b => b is not null).Select(b => b!.Player.Id).ToList();
                Assert.Equal(bases.Count, bases.Distinct().Count());
                Assert.True(outs + r.OutsRecorded < 3 || r.RunsScored == 0, what);
            }
        }
    }

    // ── Hit and run: when it is on ───────────────────────────────────────────

    [Fact]
    public void HitAndRun_IsOnOnlyWithARunnerOnFirst_AndSecondOpen()
    {
        var def = new DefensiveDecision();
        Assert.True(AtBatResolver.IsHitAndRun(Situation(true, false, false, 0), HitAndRun, def));
        Assert.True(AtBatResolver.IsHitAndRun(Situation(true, false, true, 1), HitAndRun, def));
        Assert.False(AtBatResolver.IsHitAndRun(Situation(false, false, false, 0), HitAndRun, def));   // nobody to send
        Assert.False(AtBatResolver.IsHitAndRun(Situation(true, true, false, 0), HitAndRun, def));     // second is taken
        Assert.False(AtBatResolver.IsHitAndRun(Situation(true, false, false, 0), Swing, def));
        Assert.False(AtBatResolver.IsHitAndRun(Situation(true, false, false, 0), HitAndRun,
            new DefensiveDecision { IntentionalWalk = true }));                                          // pitching around him
    }

    [Fact]
    public void HitAndRunCalledWhereItCannotWork_PlaysLikeAnOrdinaryAtBat()
    {
        var results = Resolve(Situation(true, true, false, 0), HitAndRun, 3000);

        Assert.All(results, r => Assert.False(r.HitAndRun));
        Assert.DoesNotContain(results, r => r.Outcome is OutcomeType.CaughtStealing or OutcomeType.StolenBase);
        Assert.DoesNotContain(results, r => r.Description.StartsWith("Hit and run"));
    }

    // ── Hit and run: what it does ────────────────────────────────────────────

    [Fact]
    public void HitAndRun_CutsStrikeoutsAndDoublePlays_AndTheRunnerTakesTheExtraBaseOnASingle()
    {
        var s = Situation(true, false, false, 0);
        var plain = Resolve(s, Swing, 8000);
        var sent = Resolve(s, HitAndRun, 8000);

        Assert.True(Rate(sent, r => r.Outcome == OutcomeType.Strikeout) < Rate(plain, r => r.Outcome == OutcomeType.Strikeout));
        Assert.True(Rate(sent, r => r.Outcome == OutcomeType.DoublePlay) < 0.5 * Rate(plain, r => r.Outcome == OutcomeType.DoublePlay));

        // First to third on a single: never for a runner who waits, often for one who is already going.
        var runner = s.RunnerOnFirst!.Player;
        bool toThird(PlayResult r) => r.Outcome == OutcomeType.Single && r.NewThird?.Player == runner;
        Assert.Equal(0, plain.Count(toThird));
        Assert.True(sent.Count(toThird) > 0);
    }

    [Fact]
    public void HitAndRun_CanStrikeOutTheBatterAndThrowOutTheRunner()
    {
        var s = Situation(true, false, false, 0);
        var r = Resolve(s, HitAndRun, 8000).First(x => x.Outcome == OutcomeType.Strikeout && x.OutsRecorded == 2);

        Assert.True(r.HitAndRun);
        Assert.Null(r.NewFirst);
        Assert.Null(r.NewSecond);
        var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
        Assert.Equal((2, RunnerFate.TaggedOut), (runner.ToBase, runner.Fate));
        Assert.Equal(RunnerFate.Retired, r.RunnerMoves.Single(m => m.FromBase == 0).Fate);
        Assert.Contains("caught stealing", r.Description);
        Assert.StartsWith("Hit and run:", r.Description);
    }

    [Fact]
    public void HitAndRun_CanStrikeOutTheBatterWhileTheRunnerSteals()
    {
        var s = Situation(true, false, false, 0);
        var r = Resolve(s, HitAndRun, 8000).First(x => x.Outcome == OutcomeType.Strikeout && x.OutsRecorded == 1);

        Assert.Same(s.RunnerOnFirst!.Player, r.NewSecond!.Player);
        Assert.Null(r.NewFirst);
        Assert.Contains("steals second", r.Description);
    }

    [Fact]
    public void HitAndRun_ALineDriveCanDoubleOffTheRunner()
    {
        var s = Situation(true, false, false, 0);
        var r = Resolve(s, HitAndRun, 8000).First(x => x.Outcome == OutcomeType.LineOut && x.OutsRecorded == 2);

        Assert.Null(r.NewFirst);
        Assert.Equal(0, r.RunsScored);
        var runner = r.RunnerMoves.Single(m => m.FromBase == 1);
        Assert.Equal((1, RunnerFate.TaggedOut), (runner.ToBase, runner.Fate));   // out at the base he left
        Assert.NotNull(r.Fielder);
        Assert.Contains("doubled off", r.Description);
    }

    [Fact]
    public void APitchoutBeatsAHitAndRun_ItBecomesAStealAttemptThatUsuallyFails()
    {
        var results = Resolve(Situation(true, false, false, 0), HitAndRun, 2000,
            new DefensiveDecision { PitchType = PitchType.Pitchout });

        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { OutcomeType.StolenBase, OutcomeType.CaughtStealing }));
        Assert.True(Rate(results, r => r.Outcome == OutcomeType.CaughtStealing) > 0.6);
    }

    [Fact]
    public void EveryHitAndRun_HasMovesThatAgreeWithTheResult()
    {
        foreach (var (third, outs) in new[] { (false, 0), (true, 0), (false, 1), (true, 1), (false, 2) })
        {
            var s = Situation(true, false, third, outs);
            foreach (var r in Resolve(s, HitAndRun, 800))
            {
                string what = $"{r.Outcome}: {r.Description}";
                Assert.Equal(r.RunsScored, r.RunnerMoves.Count(m => m.Fate == RunnerFate.Scored));
                Assert.Equal(r.OutsRecorded, r.RunnerMoves.Count(m => m.IsOut));
                Assert.True(r.OutsRecorded + outs <= 3 || r.OutsRecorded == 2, what);
                Assert.True(outs + r.OutsRecorded < 3 || r.RunsScored == 0, what);   // no run on the third out
                var bases = new[] { r.NewFirst, r.NewSecond, r.NewThird }.Where(b => b is not null).Select(b => b!.Player.Id).ToList();
                Assert.Equal(bases.Count, bases.Distinct().Count());
            }
        }
    }

    // ── Whole games and the box score ────────────────────────────────────────

    [Fact]
    public void InWholeGames_SacrificesAreCreditedAsSH_AndAreNotAtBats()
    {
        // The batting side bunts whenever there is a runner on first or second, so sacrifices pile up.
        var sacrifices = new Dictionary<GameService, int>();
        foreach (var (r, _, game) in Forced(Bunt, s => s.RunnerOnFirst is not null || s.RunnerOnSecond is not null, 10))
        {
            sacrifices.TryGetValue(game, out int n);
            sacrifices[game] = n + (r.Outcome == OutcomeType.SacrificeBunt ? 1 : 0);
        }

        Assert.True(sacrifices.Values.Sum() > 0);
        foreach (var (game, expected) in sacrifices)
        {
            var s = game.State!;
            Assert.Equal(expected, s.Box.Visitor.Batters.Concat(s.Box.Home.Batters).Sum(b => b.SH));
            foreach (var (box, faced) in new[] { (s.Box.Visitor, s.Box.Home), (s.Box.Home, s.Box.Visitor) })
            {
                // every plate appearance is a batter faced: at-bats, walks, hit by pitch and sacrifices
                int pa = box.Batters.Sum(b => b.AB + b.BB + b.HBP + b.SH);
                Assert.Equal(pa, faced.Pitchers.Sum(p => p.BF));
            }
        }
    }

    [Fact]
    public void ABoxScoreShowsSacrificesAsALine()
    {
        var games = Forced(Bunt, s => s.RunnerOnFirst is not null, 10).Select(p => p.Game).Distinct().ToList();

        var withSacrifice = games.First(g => g.State!.Box.Visitor.Batters.Concat(g.State.Box.Home.Batters).Any(b => b.SH > 0));
        Assert.Contains("SH:", BoxScoreFormatter.ToText(withSacrifice.State!));
    }

    [Fact]
    public void ForcedHitAndRuns_ShowUpInTheBoxScoreAsStolenBasesAndCaughtStealings()
    {
        // Every steal on the box score is either a steal play or a hit and run on which the batter struck out.
        var expected = new Dictionary<GameService, (int Sb, int Cs)>();
        int strikeoutThrowOuts = 0, strikeoutSteals = 0;
        foreach (var (r, _, game) in Forced(HitAndRun, st => st.RunnerOnFirst is not null && st.RunnerOnSecond is null, 12))
        {
            expected.TryGetValue(game, out var e);
            bool strikeoutGo = r.HitAndRun && r.Outcome == OutcomeType.Strikeout;
            if (r.Outcome == OutcomeType.StolenBase || (strikeoutGo && r.OutsRecorded < 2)) e.Sb++;
            if (r.Outcome == OutcomeType.CaughtStealing || (strikeoutGo && r.OutsRecorded >= 2)) e.Cs++;
            if (strikeoutGo) { if (r.OutsRecorded >= 2) strikeoutThrowOuts++; else strikeoutSteals++; }
            expected[game] = e;
        }

        Assert.True(strikeoutThrowOuts > 0 && strikeoutSteals > 0);
        foreach (var (game, e) in expected)
        {
            var batters = game.State!.Box.Visitor.Batters.Concat(game.State.Box.Home.Batters).ToList();
            Assert.Equal(e.Sb, batters.Sum(b => b.SB));
            Assert.Equal(e.Cs, batters.Sum(b => b.CS));
        }
    }

    // ── The pictures ─────────────────────────────────────────────────────────

    /// <summary>Plays whole games in which the batting side makes <paramref name="call"/> whenever <paramref name="when"/> holds.</summary>
    private static IEnumerable<(PlayResult Result, PrePlaySnapshot Before, GameService Game)> Forced(
        OffensiveDecision call, Func<GameState, bool> when, int games)
    {
        for (int seed = 1; seed <= games; seed++)
        {
            var game = NewGame(seed);
            var cpu = new ComputerManager(new Random(seed));
            int guard = 0;
            while (game.State!.Status == GameStatus.InProgress && guard++ < 2000)
            {
                var s = game.State!;
                var offense = when(s) ? call : cpu.MakeOffensiveDecision(s);
                var result = game.ExecuteAtBat(offense, cpu.MakeDefensiveDecision(s));
                yield return (result, game.LastPrePlay!, game);
            }
        }
    }

    private static Track TrackOf(PlayScript s, string id) => s.Tracks.Single(t => t.ActorId == id);

    private static PlayScript Script(PlayResult r, PrePlaySnapshot b, int seed = 7) => PlayChoreographer.Build(b, r, seed);

    private static string RunnerId(PlayResult r, int from) => ActorFor(r.RunnerMoves.Single(m => m.FromBase == from));

    private static string ActorFor(RunnerMove m) => m.FromBase == 0 ? "batter" : $"runner-{m.Player.Id}";

    private static bool EndsAt(PlayScript s, string id, FieldPoint spot) =>
        TrackOf(s, id).Keys[^1] is { } k && Math.Abs(k.X - spot.X) < 0.05 && Math.Abs(k.Y - spot.Y) < 0.05;

    [Fact]
    public void ABunt_IsShownWithASquaredStance_NotASwing()
    {
        var (r, b, _) = Forced(Bunt, s => s.RunnerOnFirst is not null, 12).First(p => p.Result.Outcome == OutcomeType.SacrificeBunt);
        var script = Script(r, b);

        var batter = TrackOf(script, "batter").Keys;
        Assert.Contains(batter, k => k.Pose == Poses.Bunt);
        Assert.DoesNotContain(batter, k => k.Pose == Poses.Swing);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == "BUNT!");
        // the batter is out at first, so he ends the play down the line
        Assert.Equal(Poses.Out, batter[^1].Pose);
    }

    [Fact]
    public void ABunt_StopsInFrontOfThePlate()
    {
        foreach (var (r, b, _) in Forced(Bunt, s => true, 3).Where(p => p.Result.Outcome == OutcomeType.GroundOut).Take(10))
        {
            var ball = TrackOf(Script(r, b), "ball").Keys;
            // the first stop after contact is the bunt landing, well inside the infield grass
            var landing = ball.First(k => k.T > 0.4 && k.Ease == Ease.Roll);
            Assert.True(Math.Sqrt(landing.X * landing.X + landing.Y * landing.Y) < 60, $"{landing}");
        }
    }

    [Fact]
    public void ABuntSingle_HasTheBatterSafeAtFirst_AndTheThrowLate()
    {
        var (r, b, _) = Forced(Bunt, s => s.RunnerOnFirst is null, 20).First(p => p.Result.Outcome == OutcomeType.Single && p.Result.IsBunt);
        var script = Script(r, b);

        Assert.True(EndsAt(script, "batter", FieldGeometry.First));
        Assert.NotEqual(Poses.Out, TrackOf(script, "batter").Keys[^1].Pose);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == r.Description);
    }

    [Fact]
    public void AFieldersChoice_ShowsTheRunnerOutAtTheNextBase_AndTheBatterSafeAtFirst()
    {
        var (r, b, _) = Forced(Bunt, s => s.RunnerOnFirst is not null && s.RunnerOnSecond is null, 20)
            .First(p => p.Result.Outcome == OutcomeType.FieldersChoice);
        var script = Script(r, b);

        string runner = RunnerId(r, 1);
        Assert.True(EndsAt(script, runner, FieldGeometry.Second));
        Assert.Equal(Poses.Out, TrackOf(script, runner).Keys[^1].Pose);
        Assert.True(EndsAt(script, "batter", FieldGeometry.First));
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == r.Description);
    }

    [Fact]
    public void AHitAndRun_HasTheRunnerOffBeforeTheBallReachesThePlate()
    {
        var (r, b, _) = Forced(HitAndRun, s => s.RunnerOnFirst is not null && s.RunnerOnSecond is null, 15)
            .First(p => p.Result.HitAndRun && p.Result.Outcome == OutcomeType.Single);
        var script = Script(r, b);

        double plate = TrackOf(script, "ball").Keys.First(k => k.X == 0 && k.Y == 0).T;
        var keys = TrackOf(script, RunnerId(r, 1)).Keys;
        // on the way to second (past the lead, short of the bag) when the ball gets there
        var atPlate = keys.Last(k => k.T <= plate + 0.001);
        Assert.Equal(Poses.Run, atPlate.Pose);
        double fromFirst = new FieldPoint(atPlate.X, atPlate.Y).DistanceTo(FieldGeometry.First);
        Assert.InRange(fromFirst, 15, 80);
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Banner && c.Value == "HIT AND RUN!");
    }

    [Fact]
    public void AStrikeoutThatIsAlsoACaughtStealing_ShowsTheCatchersThrowAndTheRunnerOut()
    {
        var (r, b, _) = Forced(HitAndRun, s => s.RunnerOnFirst is not null && s.RunnerOnSecond is null, 25)
            .First(p => p.Result.Outcome == OutcomeType.Strikeout && p.Result.OutsRecorded == 2);
        var script = Script(r, b);

        string runner = RunnerId(r, 1);
        Assert.Equal(Poses.Out, TrackOf(script, runner).Keys[^1].Pose);
        Assert.True(EndsAt(script, runner, FieldGeometry.Second));
        Assert.Contains(TrackOf(script, "catcher").Keys, k => k.Pose == Poses.Pop);
        Assert.Contains(TrackOf(script, "ball").Keys, k => k.Ease == Ease.Arc);   // a throw
        Assert.Contains(script.Cues, c => c.Kind == CueKind.Sound && c.Value == "strikeout");
    }

    [Fact]
    public void ALineDriveThatDoublesOffTheRunner_ShowsHimTurnAndBeThrownOutAtFirst()
    {
        var (r, b, _) = Forced(HitAndRun, s => s.RunnerOnFirst is not null && s.RunnerOnSecond is null, 25)
            .First(p => p.Result.Outcome == OutcomeType.LineOut && p.Result.OutsRecorded == 2);
        var script = Script(r, b);

        string runner = RunnerId(r, 1);
        var keys = TrackOf(script, runner).Keys;
        Assert.Equal(Poses.Out, keys[^1].Pose);
        Assert.True(EndsAt(script, runner, FieldGeometry.First));
        // he got a good way toward second before he had to turn back
        Assert.Contains(keys, k => new FieldPoint(k.X, k.Y).DistanceTo(FieldGeometry.First) > 15);
    }

    [Fact]
    public void AHitAndRunRunnerWhoHasToComeBack_IsBackOnHisBaseAtTheEnd()
    {
        var (r, b, _) = Forced(HitAndRun, s => s.RunnerOnFirst is not null && s.RunnerOnSecond is null, 20)
            .First(p => p.Result.HitAndRun && p.Result.Outcome == OutcomeType.FlyOut
                        && p.Result.RunnerMoves.Single(m => m.FromBase == 1).Fate == RunnerFate.StayedPut);
        var script = Script(r, b);

        Assert.True(EndsAt(script, RunnerId(r, 1), FieldGeometry.First));
    }

    [Fact]
    public void EveryBuntAndHitAndRunScript_IsWellFormed()
    {
        int scripts = 0;
        var plays = Forced(Bunt, s => s.RunnerOnFirst is not null || s.RunnerOnSecond is not null, 6)
            .Concat(Forced(HitAndRun, s => s.RunnerOnFirst is not null && s.RunnerOnSecond is null, 10));
        foreach (var (r, b, _) in plays.Where(p => p.Result.IsBunt || p.Result.HitAndRun))
        {
            scripts++;
            string what = $"{r.Outcome}: {r.Description}";
            var s = Script(r, b);
            foreach (var t in s.Tracks)
            {
                Assert.Equal(0, t.Keys[0].T);
                for (int i = 1; i < t.Keys.Count; i++)
                    Assert.True(t.Keys[i].T >= t.Keys[i - 1].T, $"{what}: {t.ActorId} goes back in time");
                Assert.True(t.Keys[^1].T <= s.Duration, what);
                Assert.All(t.Keys, k => Assert.True(k.Z >= 0, what));
            }
            Assert.All(s.Cues, c => Assert.InRange(c.T, 0, s.Duration));
            Assert.Contains(s.Cues, c => c.Kind == CueKind.Banner && c.Value == r.Description);

            // a runner put out at a base ends in the out pose, and nobody else does (a batter who is retired at the plate
            // just walks off)
            foreach (var m in r.RunnerMoves)
            {
                var last = TrackOf(s, ActorFor(m)).Keys[^1];
                if (m.Fate is RunnerFate.ForcedOut or RunnerFate.TaggedOut) Assert.Equal(Poses.Out, last.Pose);
                else Assert.NotEqual(Poses.Out, last.Pose);
            }
        }
        Assert.True(scripts > 20);
    }
}
