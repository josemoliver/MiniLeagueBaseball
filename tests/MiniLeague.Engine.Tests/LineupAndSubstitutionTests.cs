using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

public class LineupAndSubstitutionTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(out Team visitor, out Team home,
        IReadOnlyList<LineupSlot>? visitorLineup = null)
    {
        visitor = Load("1927-nyy.json");
        home = Load("1975-cin.json");
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)));
        game.StartGame(visitor, home, ManagerControl.Human, ManagerControl.Computer,
            visitorLineupSlots: visitorLineup);
        return game;
    }

    // ── LineupRules ──────────────────────────────────────────────────────────

    [Fact]
    public void Default_IsValidForEveryShippedTeam()
    {
        var index = TeamLoader.ParseIndex(File.ReadAllText(Path.Combine(DataDir, "teams.json")));
        foreach (var path in index)
        {
            var team = TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, path)));
            Assert.Empty(LineupRules.Validate(team, LineupRules.Default(team)));
        }
    }

    [Fact]
    public void Default_RepairsDuplicatePositions()
    {
        var team = Load("1927-nyy.json");
        foreach (var p in team.Batters) p.PrimaryPosition = Position.Left;  // everyone "LF"

        var slots = LineupRules.Default(team);

        Assert.Empty(LineupRules.Validate(team, slots));
    }

    [Fact]
    public void Validate_FlagsDuplicatePlayersAndMissingPositions()
    {
        var team = Load("1927-nyy.json");
        var slots = LineupRules.Default(team);
        slots[1] = slots[0] with { Position = slots[1].Position };  // same player twice

        var errors = LineupRules.Validate(team, slots);

        Assert.Contains(errors, e => e.Contains("more than once"));
    }

    [Fact]
    public void Validate_FlagsPositionAssignedTwice()
    {
        var team = Load("1927-nyy.json");
        var slots = LineupRules.Default(team);
        slots[1] = slots[1] with { Position = slots[0].Position };

        Assert.NotEmpty(LineupRules.Validate(team, slots));
    }

    [Fact]
    public void StartGame_RejectsInvalidCustomLineup()
    {
        var team = Load("1927-nyy.json");
        var bad = LineupRules.Default(team).Take(8).ToList();
        var game = new GameService(new AtBatResolver(), new ComputerManager());

        Assert.Throws<ArgumentException>(() =>
            game.StartGame(team, Load("1975-cin.json"), visitorLineupSlots: bad));
    }

    [Fact]
    public void StartGame_UsesCustomBattingOrderAndPositions()
    {
        var team = Load("1927-nyy.json");
        var reversed = LineupRules.Default(team).AsEnumerable().Reverse().ToList();

        var game = NewGame(out _, out _, reversed);

        Assert.Equal(reversed.Select(x => x.PlayerId), game.State!.VisitorLineup);
        Assert.Equal(reversed[0].Position, game.State.PositionsFor(true)[reversed[0].PlayerId]);
    }

    // ── Substitutions ────────────────────────────────────────────────────────

    [Fact]
    public void PinchHit_ReplacesBatterAndInheritsPosition()
    {
        var game = NewGame(out var visitor, out _);
        var s = game.State!;
        var batter = s.CurrentBatter;
        var batterPos = s.CurrentBatterPosition;
        var pinch = s.Bench(true).First();
        var lineupBefore = visitor.DefaultLineup.ToList();

        game.PinchHit(pinch.Id);

        Assert.Equal(pinch.Id, s.CurrentBatter.Id);
        Assert.Equal(batterPos, s.CurrentBatterPosition);
        Assert.Contains(batter.Id, s.VisitorUsed);
        Assert.DoesNotContain(pinch.Id, s.Bench(true).Select(p => p.Id));
        Assert.DoesNotContain(batter.Id, s.Bench(true).Select(p => p.Id));
        Assert.Contains("Pinch hitter", s.PlayByPlay[0].Description);
        Assert.Equal(lineupBefore, visitor.DefaultLineup);  // the team itself is untouched
    }

    [Fact]
    public void PinchHit_RejectsPlayerAlreadyInLineupOrRemoved()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        var removed = s.CurrentBatter.Id;
        game.PinchHit(s.Bench(true).First().Id);

        Assert.Throws<InvalidOperationException>(() => game.PinchHit(s.LineupFor(true)[1]));
        Assert.Throws<InvalidOperationException>(() => game.PinchHit(removed));
    }

    [Fact]
    public void PinchRun_ReplacesRunnerOnBase()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        var runner = s.LineupPlayers(true).Skip(3).First().Player;
        s.RunnerOnFirst = new BaseRunner { Player = runner };
        var pinch = s.Bench(true).First();

        game.PinchRun(1, pinch.Id);

        Assert.Equal(pinch.Id, s.RunnerOnFirst!.Player.Id);
        Assert.Contains(pinch.Id, s.VisitorLineup);
        Assert.DoesNotContain(runner.Id, s.VisitorLineup);
        Assert.Throws<InvalidOperationException>(() => game.PinchRun(2, s.Bench(true).First().Id));
    }

    [Fact]
    public void Replace_PutsBenchPlayerInSameSlotAndPosition()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        var target = s.LineupPlayers(true).Last();
        int slot = s.VisitorLineup.IndexOf(target.Player.Id);
        var bench = s.Bench(true).First();

        game.Replace(true, target.Player.Id, bench.Id);

        Assert.Equal(bench.Id, s.VisitorLineup[slot]);
        Assert.Equal(target.Position, s.PositionsFor(true)[bench.Id]);
    }

    [Fact]
    public void Replace_RejectsCurrentBatterAndRunners()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        var bench = s.Bench(true).First();

        Assert.Throws<InvalidOperationException>(() => game.Replace(true, s.CurrentBatter.Id, bench.Id));

        s.RunnerOnFirst = new BaseRunner { Player = s.LineupPlayers(true).Last().Player };
        Assert.Throws<InvalidOperationException>(() =>
            game.Replace(true, s.RunnerOnFirst.Player.Id, bench.Id));
    }

    [Fact]
    public void SwapPositions_TradesPositionsOnly()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        var order = s.VisitorLineup.ToList();
        var (a, b) = (order[0], order[1]);
        var (posA, posB) = (s.PositionsFor(true)[a], s.PositionsFor(true)[b]);

        game.SwapPositions(true, a, b);

        Assert.Equal(posB, s.PositionsFor(true)[a]);
        Assert.Equal(posA, s.PositionsFor(true)[b]);
        Assert.Equal(order, s.VisitorLineup);
    }

    [Fact]
    public void ReplacingTheCatcher_ChangesWhoCatchesForTheDefense()
    {
        var game = NewGame(out _, out var home);
        var s = game.State!;
        var catcher = s.LineupPlayers(false).First(x => x.Position == Position.C).Player;
        var bench = s.Bench(false).First();
        s.IsTopOfInning = true;  // home team is on defense
        s.HomeControl = ManagerControl.Human;  // a computer manager would rearrange the defense afterwards

        game.Replace(false, catcher.Id, bench.Id);

        Assert.Equal(bench.Id, s.DefenderAt(Position.C)!.Id);
        Assert.Equal(catcher.Id, home.Roster.First(p => p.Id == catcher.Id).Id);
    }

    // ── Computer manager ─────────────────────────────────────────────────────

    [Fact]
    public void Computer_MovesAGoodFielderBehindThePlateWhenTheCatcherIsReplacedByANonCatcher()
    {
        var game = NewGame(out _, out var home);
        var s = game.State!;
        var catcher = s.LineupPlayers(false).First(x => x.Position == Position.C).Player;
        // A bench player who has never caught.
        var bench = s.Bench(false).First(p => p.HasFieldingData && !p.IsNaturalAt(Position.C));

        game.Replace(false, catcher.Id, bench.Id);   // home team is computer-managed

        var newCatcher = s.DefenderAt(Position.C)!;
        Assert.True(newCatcher.IsNaturalAt(Position.C) || newCatcher.Id != bench.Id,
            "the computer should not leave a non-catcher at catcher when a better arrangement exists");
        Assert.Contains(s.PlayByPlay.Take(4), e => e.Description.Contains("moves to"));
    }

    [Fact]
    public void Computer_PinchHitsWeakBatterLateInCloseGame()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        s.CurrentInning = 8;
        s.CurrentBatter.Overall = new BatterStats { AtBats = 300, Hits = 50, Walks = 10 };
        var bench = s.Bench(true).First();
        bench.Overall = new BatterStats { AtBats = 300, Hits = 120, HomeRuns = 30, Walks = 60 };

        var decision = new ComputerManager(new Random(1)).MakeOffensiveDecision(s);

        Assert.Equal(bench.Id, decision.PinchHitterPlayerId);
    }

    [Fact]
    public void Computer_PinchRunsForSlowRunnerWhenOneRunMatters()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        s.CurrentInning = 9;
        var runner = s.LineupPlayers(true).Skip(4).First().Player;
        runner.SpeedRating = 1;
        s.RunnerOnFirst = new BaseRunner { Player = runner };
        var fast = s.Bench(true).First();
        fast.SpeedRating = 5;

        var decision = new ComputerManager(new Random(1)).MakeOffensiveDecision(s);

        Assert.Equal(fast.Id, decision.PinchRunnerPlayerId);
        Assert.Equal(1, decision.PinchRunnerBaseIndex);
    }

    [Fact]
    public void ExecuteAtBat_AppliesDecisionSubstitutionsBeforeResolving()
    {
        var game = NewGame(out _, out _);
        var s = game.State!;
        var pinch = s.Bench(true).First();

        game.ExecuteAtBat(new OffensiveDecision { PinchHitterPlayerId = pinch.Id }, new DefensiveDecision());

        Assert.Contains(pinch.Id, s.VisitorLineup);
        Assert.Contains("Pinch hitter", s.PlayByPlay.Last(e => e.Description.StartsWith("NYY: Pinch")).Description);
    }
}
