using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

public class PlayHintTests
{
    private static GameService NewGame()
    {
        string root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "MiniLeagueBaseball.slnx"))) root = Directory.GetParent(root)!.FullName;
        string dir = Path.Combine(root, "src", "MiniLeague.Web", "wwwroot", "data", "teams");
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(dir, file)));
        var service = new GameService(new AtBatResolver(new Random(11)), new ComputerManager(new Random(11)));
        service.StartGame(Load("1927-nyy.json"), Load("1955-bro.json"), ManagerControl.Human, ManagerControl.Human);
        return service;
    }

    private static GameState Game() => NewGame().State!;

    [Fact]
    public void HintIsRepeatableReadOnlyAndHasNoDigits()
    {
        var state = Game();
        state.CurrentPitcher.PitchCount = 125;
        state.CurrentPitcher.Condition = PitcherCondition.Tired;
        state.RunnerOnFirst = new BaseRunner { Player = state.OffenseTeam.Roster[0] };
        string before = GameSnapshots.ToJson(GameSnapshots.Capture(state));
        foreach (var side in new[] { PlayHintSide.Offense, PlayHintSide.Defense })
        {
            var first = PlayHintAdvisor.Get(state, side);
            var again = PlayHintAdvisor.Get(state, side);
            Assert.Equal(first, again);
            string card = string.Join(' ', first.Situation, first.Matchup, first.PitcherNote,
                first.PlatoonNote, first.LeverageNote, first.Call, first.Reason, first.Conditional);
            Assert.DoesNotMatch(new Regex(@"\d"), card);
            Assert.NotEmpty(first.Reason);
        }
        Assert.Equal(before, GameSnapshots.ToJson(GameSnapshots.Capture(state)));
    }

    [Fact]
    public void AdviceUsesOnlyAvailableControls()
    {
        var state = Game();
        for (int inning = 1; inning <= 9; inning += 4)
        for (int outs = 0; outs < 3; outs++)
        for (int bases = 0; bases < 8; bases++)
        {
            state.CurrentInning = inning;
            state.Outs = outs;
            state.RunnerOnFirst = (bases & 1) == 0 ? null : new BaseRunner { Player = state.OffenseTeam.Roster[0] };
            state.RunnerOnSecond = (bases & 2) == 0 ? null : new BaseRunner { Player = state.OffenseTeam.Roster[1] };
            state.RunnerOnThird = (bases & 4) == 0 ? null : new BaseRunner { Player = state.OffenseTeam.Roster[2] };
            var batting = PlayHintAdvisor.Get(state, PlayHintSide.Offense);
            var fielding = PlayHintAdvisor.Get(state, PlayHintSide.Defense);
            Assert.NotEqual(OffensiveStrategy.SwingSafe, batting.Strategy);
            Assert.NotEqual(OffensiveStrategy.SurpriseBunt, batting.Strategy);
            Assert.NotEqual(PitchType.Pitchout, fielding.Pitch);
            if (batting.Strategy == OffensiveStrategy.Steal)
                Assert.NotEqual(StealPlan.None, RunningRules.StealPlanFor(state));
            if (batting.Strategy == OffensiveStrategy.HitAndRun)
                Assert.True(state.RunnerOnFirst is not null && state.RunnerOnSecond is null);
            if (batting.Strategy is OffensiveStrategy.Squeeze or OffensiveStrategy.SafetySqueeze)
                Assert.True(state.RunnerOnThird is not null && state.Outs < 2);
        }
    }

    [Fact]
    public void MissingLineupFallsBackToASelectableCall()
    {
        var state = Game();
        state.VisitorLineup.Clear();
        var batting = PlayHintAdvisor.Get(state, PlayHintSide.Offense);
        var fielding = PlayHintAdvisor.Get(state, PlayHintSide.Defense);
        Assert.Equal(OffensiveStrategy.SwingAway, batting.Strategy);
        Assert.Equal(PitchType.Fastball, fielding.Pitch);
        Assert.Equal(DefensiveAlignment.Standard, fielding.Alignment);
    }

    [Fact]
    public void HintSettingSurvivesSavingAndOlderSavesDefaultToEnabled()
    {
        var state = Game();
        state.Options = state.Options with { ShowPlayHints = false };
        var saved = GameSnapshots.FromJson(GameSnapshots.ToJson(GameSnapshots.Capture(state)));
        Assert.False(saved!.Options.ShowPlayHints);

        var oldJson = JsonNode.Parse(GameSnapshots.ToJson(GameSnapshots.Capture(state)))!;
        oldJson["Options"]!.AsObject().Remove("ShowPlayHints");
        oldJson.AsObject().Remove("VisitorHintsUsed");
        oldJson.AsObject().Remove("HomeHintsUsed");
        oldJson.AsObject().Remove("VisitorLastHintPlay");
        oldJson.AsObject().Remove("HomeLastHintPlay");
        var older = GameSnapshots.FromJson(oldJson.ToJsonString());
        Assert.True(older!.Options.ShowPlayHints);
        Assert.Equal(0, older.VisitorHintsUsed);
        Assert.Equal(-1, older.VisitorLastHintPlay);
    }

    [Fact]
    public void EachHumanTeamGetsTenHintsAndReopeningTheCurrentPlayIsFree()
    {
        var game = NewGame();
        for (int use = 1; use <= GameService.MaxPlayHints; use++)
        {
            Assert.True(game.TryUsePlayHint(true));
            Assert.True(game.TryUsePlayHint(true));
            Assert.True(game.TryUsePlayHint(false));
            Assert.Equal(use, game.State!.VisitorHintsUsed);
            Assert.Equal(use, game.State.HomeHintsUsed);
            Assert.Equal(GameService.MaxPlayHints - use, game.PlayHintsRemaining(true));
            Assert.Equal(GameService.MaxPlayHints - use, game.PlayHintsRemaining(false));
            if (use < GameService.MaxPlayHints)
                game.ExecuteAtBat(new OffensiveDecision(), new DefensiveDecision());
        }

        Assert.True(game.CanUsePlayHint(true)); // the last hint can still be reopened
        var snapshot = GameSnapshots.FromJson(GameSnapshots.ToJson(GameSnapshots.Capture(game.State!)))!;
        var visitor = game.State!.VisitorTeam;
        var home = game.State.HomeTeam;
        var resumed = new GameService(new AtBatResolver(new Random(11)), new ComputerManager(new Random(11)));
        resumed.Resume(snapshot, id => id == visitor.Id ? visitor : id == home.Id ? home : null);
        Assert.True(resumed.TryUsePlayHint(true));
        Assert.Equal(GameService.MaxPlayHints, resumed.State!.VisitorHintsUsed);

        resumed.ExecuteAtBat(new OffensiveDecision(), new DefensiveDecision());
        Assert.False(resumed.CanUsePlayHint(true));
        Assert.False(resumed.CanUsePlayHint(false));
        Assert.False(resumed.TryUsePlayHint(true));
    }

    [Fact]
    public void DisabledHintsNeverSpendTheAllowance()
    {
        var game = NewGame();
        game.State!.Options = game.State.Options with { ShowPlayHints = false };
        Assert.False(game.TryUsePlayHint(true));
        Assert.Equal(0, game.State.VisitorHintsUsed);
    }
}
