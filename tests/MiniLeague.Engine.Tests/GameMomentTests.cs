using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The moments the engine announces for the ballpark organist.</summary>
public class GameMomentTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) =>
        TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(GameOptions? options = null, Random? events = null)
    {
        var game = new GameService(new AtBatResolver(new Random(1)), new ComputerManager(new Random(1)), events ?? new Random(1));
        game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer,
            options: options);
        return game;
    }

    private static PlayResult Out() => new() { Outcome = OutcomeType.GroundOut, OutsRecorded = 1, PitchesThrown = 3 };
    private static PlayResult Homer(int runs) => new() { Outcome = OutcomeType.HomeRun, RunsScored = runs, PitchesThrown = 3 };

    [Fact]
    public void ThereAreNoMomentsAtTheStartOfAGame_AndTakingThemClearsThem()
    {
        var game = NewGame();
        Assert.Empty(game.TakeMoments());

        game.ApplyResult(Homer(3));
        Assert.Single(game.TakeMoments());
        Assert.Empty(game.TakeMoments());   // already taken
    }

    // ── Rally ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheThirdRunOfAHalfInning_IsARally_ButNotTheFirstSecondOrFourth()
    {
        var game = NewGame();

        game.ApplyResult(Homer(1));
        Assert.Empty(game.TakeMoments());
        game.ApplyResult(Homer(1));
        Assert.Empty(game.TakeMoments());
        game.ApplyResult(Homer(1));                       // the third run
        Assert.Equal([GameMoment.Rally], game.TakeMoments());
        game.ApplyResult(Homer(1));                       // the fourth: already a rally
        Assert.Empty(game.TakeMoments());
    }

    [Fact]
    public void ABigInningScoredAtOnce_IsOneRally_AndRunsAreCountedPerHalfInning()
    {
        var game = NewGame();
        var s = game.State!;
        game.ApplyResult(Homer(4));                       // a grand slam
        Assert.Equal([GameMoment.Rally], game.TakeMoments());

        // two runs this half-inning for the home team, then two in a later one: never three in one
        s.IsTopOfInning = false;
        game.ApplyResult(Homer(2));
        Assert.Empty(game.TakeMoments());
        s.IsTopOfInning = true; s.CurrentInning = 2;
        game.ApplyResult(Homer(2));
        Assert.Empty(game.TakeMoments());
    }

    [Fact]
    public void EitherTeamCanStartARally()
    {
        var game = NewGame();
        game.State!.IsTopOfInning = false;

        game.ApplyResult(Homer(3));

        Assert.Equal([GameMoment.Rally], game.TakeMoments());
    }

    // ── Seventh-inning stretch ───────────────────────────────────────────────

    [Fact]
    public void TheStretchComesBetweenTheTopAndBottomOfTheSeventh_AndNeverAtAnyOtherTime()
    {
        var game = NewGame();
        var s = game.State!;

        foreach (int inning in new[] { 1, 6, 8, 9 })
        {
            s.CurrentInning = inning; s.IsTopOfInning = true; s.Outs = 2;
            game.ApplyResult(Out());
            Assert.DoesNotContain(GameMoment.SeventhInningStretch, game.TakeMoments());
        }

        s.CurrentInning = 7; s.IsTopOfInning = true; s.Outs = 2;
        game.ApplyResult(Out());
        Assert.Equal([GameMoment.SeventhInningStretch], game.TakeMoments());
        Assert.False(s.IsTopOfInning);

        s.Outs = 2;   // and the end of the bottom half is not another stretch
        game.ApplyResult(Out());
        Assert.Empty(game.TakeMoments());
    }

    [Fact]
    public void EveryFullGameHasExactlyOneStretch()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var rng = new Random(seed);
            var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng), rng);
            game.StartGame(Load("1927-nyy.json"), Load("1975-bos.json"), ManagerControl.Computer, ManagerControl.Computer);
            var all = new List<GameMoment>();
            while (game.State!.Status == GameStatus.InProgress)
            {
                game.ExecuteComputerTurn();
                all.AddRange(game.TakeMoments());
            }

            Assert.Equal(1, all.Count(m => m == GameMoment.SeventhInningStretch));
            Assert.True(all.Count(m => m == GameMoment.Rally) <= 12);
        }
    }

    // ── Pitching change and ejection ─────────────────────────────────────────

    [Fact]
    public void AnyPitchingChange_IsAMoment_WhoeverMakesIt()
    {
        var game = NewGame();
        var s = game.State!;

        game.ChangePitcher(false, s.HomeBullpen[0].Player.Id);

        Assert.Equal([GameMoment.PitchingChange], game.TakeMoments());
    }

    [Fact]
    public void AnInjuredPitchersReplacement_AlsoCountsAsAPitchingChange()
    {
        // trigger on every check, then land the injury on the pitcher
        var dice = new ScriptedDice(0.0, 0.3);
        var game = NewGame(new GameOptions(true, false, false), dice);

        game.MaybeInjurePlayer(Out(), wasTop: true, batter: game.State!.CurrentBatter);

        Assert.Contains(GameMoment.PitchingChange, game.TakeMoments());
    }

    [Fact]
    public void ARealEjection_IsAMoment_ButAMissedDisputeIsNot()
    {
        var game = NewGame(new GameOptions(false, false, true), new ScriptedDice(0.0));
        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Single }, wasTop: true);
        Assert.Empty(game.TakeMoments());

        game.MaybeEjectManager(new PlayResult { Outcome = OutcomeType.Strikeout }, wasTop: true);
        Assert.Equal([GameMoment.ManagerEjected], game.TakeMoments());
    }

    [Fact]
    public void MomentsAreNotCarriedOverIntoANewOrResumedGame()
    {
        var game = NewGame();
        game.ApplyResult(Homer(3));
        var snapshot = GameSnapshots.Capture(game.State!);

        var visitor = game.State!.VisitorTeam; var home = game.State.HomeTeam;
        game.Resume(snapshot, id => id == visitor.Id ? visitor : home);

        Assert.Empty(game.TakeMoments());
    }

    private sealed class ScriptedDice(params double[] values) : Random
    {
        private int _next;
        public override double NextDouble() => values[_next++ % values.Length];
        public override int Next(int minValue, int maxValue) => minValue;
    }
}
