using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

public class ComputerManagerDecisionTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static GameState Game()
    {
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)));
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"),
            ManagerControl.Computer, ManagerControl.Computer, options: GameOptions.None);
        return game.State!;
    }

    [Fact]
    public void StealDependsOnRunnerAndBatteryRatherThanSpeedAlone()
    {
        var state = Game();
        state.CurrentInning = 8;
        var runner = state.LineupPlayers(true).Skip(4).First().Player;
        state.RunnerOnFirst = new BaseRunner { Player = runner };
        runner.SpeedRating = 5;
        runner.Overall = new BatterStats { AtBats = 300, Hits = 80, StolenBases = 40, CaughtStealing = 3 };
        var catcher = state.DefenderAt(Position.C)!;
        catcher.HasFieldingData = false;
        catcher.ArmStrength = 1;
        state.HomePitcher.Player.PitchingOverall!.HoldRunners = 1;
        state.HomePitcher.Player.PitchingVsLeft = null;
        state.HomePitcher.Player.PitchingVsRight = null;

        state.CurrentBatter.Overall = new BatterStats { AtBats = 300, Hits = 30, Strikeouts = 180 };
        var benchBat = state.Bench(true).First();
        benchBat.Overall = new BatterStats { AtBats = 300, Hits = 130, HomeRuns = 35, Walks = 70 };
        var goodCall = new ComputerManager(new Random(2)).MakeOffensiveDecision(state);
        Assert.Equal(OffensiveStrategy.Steal, goodCall.Strategy);
        Assert.Null(goodCall.PinchHitterPlayerId);

        catcher.ArmStrength = 5;
        state.HomePitcher.Player.PitchingOverall.HoldRunners = 5;
        runner.Overall.StolenBases = 3;
        runner.Overall.CaughtStealing = 17;
        Assert.NotEqual(OffensiveStrategy.Steal, new ComputerManager(new Random(2)).MakeOffensiveDecision(state).Strategy);

        catcher.ArmStrength = 1;
        state.HomePitcher.Player.PitchingOverall.HoldRunners = 1;
        runner.Overall.StolenBases = 40;
        runner.Overall.CaughtStealing = 3;
        state.VisitorScoreByInning[0] = 2;
        Assert.NotEqual(OffensiveStrategy.Steal, new ComputerManager(new Random(2)).MakeOffensiveDecision(state).Strategy);
    }

    [Fact]
    public void AWeakBatterMayBuntButNotWhenTwoRunsAreNeededLate()
    {
        var state = Game();
        var runner = state.LineupPlayers(true).Skip(4).First().Player;
        state.RunnerOnFirst = new BaseRunner { Player = runner };
        var batter = state.CurrentBatter;
        batter.Overall = new BatterStats { AtBats = 300, Hits = 10, Strikeouts = 210 };
        batter.VsLeft = null;
        batter.VsRight = null;
        batter.BuntingAbility = 6;
        batter.SacrificeAbility = 6;

        Assert.Equal(OffensiveStrategy.SacrificeBunt,
            new ComputerManager(new Random(2)).MakeOffensiveDecision(state).Strategy);

        state.CurrentInning = 8;
        state.HomeScoreByInning[0] = 2;
        Assert.NotEqual(OffensiveStrategy.SacrificeBunt,
            new ComputerManager(new Random(2)).MakeOffensiveDecision(state).Strategy);
    }

    [Fact]
    public void IntentionalWalkRequiresAWeakerHitterOnDeck()
    {
        var state = Game();
        state.CurrentInning = 9;
        state.Outs = 1;
        state.RunnerOnSecond = new BaseRunner { Player = state.LineupPlayers(true).Skip(4).First().Player };
        var batter = state.CurrentBatter;
        var next = state.VisitorTeam.Roster.First(p => p.Id == state.VisitorLineup[1]);
        batter.Overall = new BatterStats { AtBats = 400, Hits = 180, HomeRuns = 55, Walks = 100 };
        batter.VsLeft = null;
        batter.VsRight = null;
        next.Overall = new BatterStats { AtBats = 400, Hits = 55, HomeRuns = 1, Walks = 15 };

        Assert.True(new ComputerManager(new Random(2)).MakeDefensiveDecision(state).IntentionalWalk);

        next.Overall = new BatterStats { AtBats = 400, Hits = 180, HomeRuns = 55, Walks = 100 };
        Assert.False(new ComputerManager(new Random(2)).MakeDefensiveDecision(state).IntentionalWalk);
    }

    [Fact]
    public void PitchChoiceRespondsToTheBattersOutcomeProfile()
    {
        var state = Game();
        var batter = state.CurrentBatter;
        batter.VsLeft = null;
        batter.VsRight = null;
        batter.PowerRating = 4;
        batter.Overall = new BatterStats { AtBats = 400, Hits = 160, HomeRuns = 80,
            Walks = 25, Strikeouts = 15 };
        var againstPower = new ComputerManager(new Random(3)).MakeDefensiveDecision(state).PitchType;

        batter.PowerRating = 1;
        batter.Overall = new BatterStats { AtBats = 400, Hits = 65, HomeRuns = 0,
            Walks = 25, Strikeouts = 220 };
        var againstStrikeouts = new ComputerManager(new Random(3)).MakeDefensiveDecision(state).PitchType;

        Assert.NotEqual(againstPower, againstStrikeouts);
    }

    [Fact]
    public void AReadyRelieverWithTheBetterMatchupBeatsATheBetterOverallEra()
    {
        var state = Game();
        var candidates = state.HomeBullpen.Where(w => w.Player.Role != PitcherRole.Closer).Take(2).ToList();
        Assert.Equal(2, candidates.Count);
        state.HomePitcher.PitchCount = 200;
        state.HomePitcher.BattersFaced = 30;
        foreach (var w in candidates) { w.IsWarming = true; w.BattersFacedWarming = 5; }
        candidates[0].Player.PitchingOverall!.ERA = 2;
        candidates[0].Player.PitchingVsLeft = new PitcherStats { ERA = 8, WalksAllowed = 80, InningsPitched = 100 };
        candidates[0].Player.PitchingVsRight = candidates[0].Player.PitchingVsLeft;
        candidates[1].Player.PitchingOverall!.ERA = 4;
        candidates[1].Player.PitchingVsLeft = new PitcherStats { ERA = 1, WalksAllowed = 10, InningsPitched = 100,
            Strikeouts = 100 };
        candidates[1].Player.PitchingVsRight = candidates[1].Player.PitchingVsLeft;

        Assert.Equal(candidates[1].Player.Id,
            new ComputerManager(new Random(2)).DecidePitching(state, visitor: false).ChangeToId);
    }

    [Fact]
    public void AnOverwarmedRelieverDoesNotPreventAnotherArmFromWarming()
    {
        var state = Game();
        state.HomePitcher.PitchCount = 100;
        state.HomePitcher.BattersFaced = 20;
        var stale = state.HomeBullpen[0];
        stale.IsWarming = true;
        stale.BattersFacedWarming = 12;

        var move = new ComputerManager(new Random(2)).DecidePitching(state, visitor: false);

        Assert.NotNull(move.StartWarmingId);
        Assert.NotEqual(stale.Player.Id, move.StartWarmingId);
    }

    // A runner the offense would send: fast, a long successful record, a weak battery, a tied game in the 8th.
    private static GameState RunningSituation(int speed = 5)
    {
        var state = Game();
        state.CurrentInning = 8;
        var runner = state.LineupPlayers(true).Skip(4).First().Player;
        runner.SpeedRating = speed;
        runner.Overall = new BatterStats { AtBats = 300, Hits = 80, StolenBases = 40, CaughtStealing = 3 };
        state.RunnerOnFirst = new BaseRunner { Player = runner };
        var catcher = state.DefenderAt(Position.C)!;
        catcher.HasFieldingData = false;
        catcher.ArmStrength = 1;
        state.HomePitcher.Player.PitchingOverall!.HoldRunners = 1;
        state.HomePitcher.Player.PitchingVsLeft = null;
        state.HomePitcher.Player.PitchingVsRight = null;
        return state;
    }

    private static int Pitchouts(GameState state, int calls) => Enumerable.Range(0, calls).Count(seed =>
        new ComputerManager(new Random(seed)).MakeDefensiveDecision(state).PitchType == PitchType.Pitchout);

    [Fact]
    public void NoPitchoutIsCalledAgainstARunnerWhoWillNotGo()
    {
        // Speed 1 with a poor record: the offense would never send him, so a wasted ball buys nothing.
        var state = RunningSituation(speed: 1);
        state.RunnerOnFirst!.Player.Overall = new BatterStats { AtBats = 300, Hits = 80, StolenBases = 2, CaughtStealing = 12 };

        Assert.Equal(0, Pitchouts(state, 200));
    }

    [Fact]
    public void APitchoutAgainstALikelyStealerIsMixedInRatherThanThrownEveryTime()
    {
        // Regression: the pitchout once looked like the best pitch in the pitch table, so the computer threw one
        // against every fast runner and each steal attempt ran into it.
        int pitchouts = Pitchouts(RunningSituation(), 200);

        Assert.InRange(pitchouts, 1, 100);   // sometimes, never nearly always (about 30% expected)
    }

    [Fact]
    public void ComputerPitchoutsAreRareOverWholeGames()
    {
        // Regression for the symptom: about 3.8 pitchouts per game, so every hit and run was thrown out.
        int pitchouts = 0, plays = 0, caught = 0;
        const int games = 60;
        for (int seed = 0; seed < games; seed++)
        {
            var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed + 1000)));
            Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));
            game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"),
                ManagerControl.Computer, ManagerControl.Computer, options: GameOptions.None);
            var state = game.State!;
            for (int guard = 0; state.Status == GameStatus.InProgress && guard < 3000; guard++)
            {
                var play = game.ExecuteComputerTurn();
                if (play is null) break;
                plays++;
                if (play.Pitch == PitchType.Pitchout) pitchouts++;
                if (play.Outcome == OutcomeType.CaughtStealing) caught++;
            }
        }

        Assert.True(plays > 0);
        Assert.True(pitchouts <= games * 0.5, $"{pitchouts} pitchouts in {games} games");
        Assert.True(caught <= games * 0.15, $"{caught} caught stealing in {games} games");
    }
}
