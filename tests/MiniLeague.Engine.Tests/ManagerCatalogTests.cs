using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;

namespace MiniLeague.Engine.Tests;

/// <summary>The choice of computer managers: the catalog, each persona's style, and a different one on each side.</summary>
public class ManagerCatalogTests
{
    private static string DataDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));

    private static GameService NewGame(ManagerCatalog catalog, string? visitorId, string? homeId, int seed = 1,
        ManagerControl visitor = ManagerControl.Computer, ManagerControl home = ManagerControl.Computer)
    {
        var game = new GameService(new AtBatResolver(new Random(seed)), new ComputerManager(new Random(seed)),
            new Random(seed), catalog);
        game.StartGame(Load("1927-nyy.json"), Load("1975-cin.json"), visitor, home, options: GameOptions.None,
            visitorManagerId: visitorId, homeManagerId: homeId);
        return game;
    }

    // ── The catalog ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryPersonaHasAnIdAProfileAndBuildsAManager()
    {
        var catalog = ManagerCatalog.Default;

        Assert.True(catalog.All.Count >= 5);
        Assert.Equal(catalog.All.Count, catalog.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(catalog.All.Count, catalog.All.Select(p => p.Name).Distinct().Count());
        foreach (var p in catalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Nickname));
            Assert.False(string.IsNullOrWhiteSpace(p.Style));
            Assert.False(string.IsNullOrWhiteSpace(p.Tagline));
            Assert.True(p.Biography.Length > 40, p.Id);
            Assert.True(p.Traits.Count >= 3, p.Id);
            Assert.All(p.Traits, t => Assert.InRange(t.Rating, 1, 5));
            Assert.NotNull(p.Create(new Random(1)));
        }
    }

    [Fact]
    public void TheBuiltInCatalogIsNotEmpty_EvenThoughItIsBuiltFromAStaticList()
    {
        // Regression: Default was once initialized before the list it copies, which left it empty.
        Assert.NotEmpty(ManagerCatalog.Default.All);
        Assert.Equal(ManagerCatalog.BuiltIn.Count, ManagerCatalog.Default.All.Count);
    }

    [Fact]
    public void TheDefaultManagerExists_AndAnUnknownIdFallsBackToIt()
    {
        var catalog = ManagerCatalog.Default;

        Assert.NotNull(catalog.Find(ManagerCatalog.DefaultId));
        Assert.Null(catalog.Find("nobody"));
        Assert.Null(catalog.Find(null));
        Assert.Equal(ManagerCatalog.DefaultId, catalog.Get("nobody").Id);   // a game saved by a newer version still opens
        Assert.Equal("smallball", catalog.Find("SMALLBALL")!.Id);
    }

    [Fact]
    public void TheOldRuleBasedManagerIsOneOfTheChoices()
    {
        Assert.IsType<OldSchoolManager>(ManagerCatalog.Default.Create("oldschool", new Random(1)));
        Assert.IsType<ComputerManager>(ManagerCatalog.Default.Create("analyst", new Random(1)));
    }

    [Fact]
    public void AnotherManagerCanBeAdded_ButNotWithAnIdThatIsTaken()
    {
        var catalog = new ManagerCatalog(ManagerCatalog.BuiltIn);
        var added = new ManagerPersona("test-manager", "Pat Example", "The Test", "Whatever", "A test manager.",
            "Exists only to prove the catalog can be extended without touching anything else.",
            [new ManagerTrait("Everything", 3)], rng => new ComputerManager(rng));

        catalog.Register(added);

        Assert.Same(added, catalog.Find("test-manager"));
        Assert.Equal(ManagerCatalog.BuiltIn.Count + 1, catalog.All.Count);
        Assert.Throws<ArgumentException>(() => catalog.Register(added with { Name = "Someone Else" }));
        Assert.Equal(ManagerCatalog.BuiltIn.Count, ManagerCatalog.Default.All.Count);   // the app's catalog is untouched
    }

    // ── A different manager on each side ─────────────────────────────────────

    /// <summary>A manager that records which side of the ball each of its calls was made for.</summary>
    private sealed class SpyManager(Random rng) : ComputerManager(rng)
    {
        public List<bool> OffenseCalls { get; } = [];   // IsTopOfInning at each offensive call
        public List<bool> DefenseCalls { get; } = [];
        public List<bool> PitchingCalls { get; } = [];  // the side asked about

        public override OffensiveDecision MakeOffensiveDecision(GameState state)
        {
            OffenseCalls.Add(state.IsTopOfInning);
            return base.MakeOffensiveDecision(state);
        }

        public override DefensiveDecision MakeDefensiveDecision(GameState state, Player? plannedBatter = null)
        {
            DefenseCalls.Add(state.IsTopOfInning);
            return base.MakeDefensiveDecision(state, plannedBatter);
        }

        public override PitchingMove DecidePitching(GameState state, bool visitor)
        {
            PitchingCalls.Add(visitor);
            return base.DecidePitching(state, visitor);
        }
    }

    [Fact]
    public void EachSideIsManagedByItsOwnPersona()
    {
        SpyManager? visitorSpy = null, homeSpy = null;
        var catalog = new ManagerCatalog(
        [
            ManagerCatalog.BuiltIn[0] with { Id = "spy-v", Create = r => visitorSpy = new SpyManager(r) },
            ManagerCatalog.BuiltIn[0] with { Id = "spy-h", Create = r => homeSpy = new SpyManager(r) },
        ]);
        var game = NewGame(catalog, "spy-v", "spy-h");

        for (int i = 0; i < 150 && game.State!.Status == GameStatus.InProgress; i++) game.ExecuteComputerTurn();

        Assert.NotNull(visitorSpy);
        Assert.NotNull(homeSpy);
        // The visitors bat in the top half: their manager makes the offensive calls then, and the defensive ones in the bottom.
        Assert.NotEmpty(visitorSpy!.OffenseCalls);
        Assert.All(visitorSpy.OffenseCalls, top => Assert.True(top));
        Assert.All(visitorSpy.DefenseCalls, top => Assert.False(top));
        Assert.All(visitorSpy.PitchingCalls, visitor => Assert.True(visitor));
        Assert.NotEmpty(homeSpy!.OffenseCalls);
        Assert.All(homeSpy.OffenseCalls, top => Assert.False(top));
        Assert.All(homeSpy.DefenseCalls, top => Assert.True(top));
        Assert.All(homeSpy.PitchingCalls, visitor => Assert.False(visitor));
    }

    [Fact]
    public void ASideWithNoPersonaUsesTheManagerTheGameWasGiven()
    {
        var catalog = ManagerCatalog.Default;
        var game = NewGame(catalog, visitorId: null, homeId: null);

        for (int i = 0; i < 20; i++) game.ExecuteComputerTurn();

        Assert.True(game.State!.CurrentInning >= 1);
        Assert.Equal(ManagerCatalog.DefaultId, game.PersonaFor(visitor: true)!.Id);
    }

    [Fact]
    public void ThePersonasAreSavedWithTheGame_AndAnOlderSaveWithoutThemStillOpens()
    {
        var game = NewGame(ManagerCatalog.Default, "smallball", "slugger");
        for (int i = 0; i < 12; i++) game.ExecuteComputerTurn();

        var json = GameSnapshots.ToJson(GameSnapshots.Capture(game.State!));
        var teams = new[] { Load("1927-nyy.json"), Load("1975-cin.json") };
        var resumed = new GameService(new AtBatResolver(new Random(2)), new ComputerManager(new Random(2)),
            new Random(2), ManagerCatalog.Default);
        resumed.Resume(GameSnapshots.FromJson(json)!, id => teams.FirstOrDefault(t => t.Id == id));

        Assert.Equal("smallball", resumed.State!.VisitorManagerId);
        Assert.Equal("slugger", resumed.State.HomeManagerId);
        Assert.Equal("smallball", resumed.PersonaFor(visitor: true)!.Id);
        Assert.Equal("slugger", resumed.PersonaFor(visitor: false)!.Id);

        // A save from before there was a choice has no ids at all.
        var old = GameSnapshots.FromJson(json)!;
        old.VisitorManagerId = null;
        old.HomeManagerId = null;
        var legacy = new GameService(new AtBatResolver(new Random(3)), new ComputerManager(new Random(3)),
            new Random(3), ManagerCatalog.Default);
        legacy.Resume(old, id => teams.FirstOrDefault(t => t.Id == id));
        Assert.Equal(ManagerCatalog.DefaultId, legacy.PersonaFor(visitor: true)!.Id);
        legacy.ExecuteComputerTurn();
    }

    [Fact]
    public void ComputerDefenseUsesTheFieldingSidesPersona()
    {
        // The home team fields in the top half; give it a spy and ask for its defense through the service.
        SpyManager? homeSpy = null;
        var catalog = new ManagerCatalog(
        [
            ManagerCatalog.BuiltIn[0],
            ManagerCatalog.BuiltIn[0] with { Id = "spy", Create = r => homeSpy = new SpyManager(r) },
        ]);
        var game = NewGame(catalog, ManagerCatalog.DefaultId, "spy", visitor: ManagerControl.Human, home: ManagerControl.Computer);

        game.GetComputerDefense();

        Assert.Single(homeSpy!.DefenseCalls);
        Assert.True(homeSpy.DefenseCalls[0]);
    }

    // ── Each persona manages in its own way ──────────────────────────────────

    /// <summary>Bunts, steals and hit-and-runs a manager's side attempts per game, over whole games against the Analyst.</summary>
    private static double SmallBallPerGame(string personaId, int games)
    {
        int smallBall = 0;
        for (int seed = 0; seed < games; seed++)
        {
            var game = NewGame(ManagerCatalog.Default, personaId, ManagerCatalog.DefaultId, seed);
            var state = game.State!;
            for (int guard = 0; state.Status == GameStatus.InProgress && guard < 3000; guard++)
            {
                bool visitorBats = state.IsTopOfInning;
                var play = game.ExecuteComputerTurn();
                if (play is null) break;
                if (visitorBats && (play.Outcome is OutcomeType.SacrificeBunt or OutcomeType.StolenBase
                        or OutcomeType.CaughtStealing || play.HitAndRun || play.IsBunt)) smallBall++;
            }
        }
        return (double)smallBall / games;
    }

    [Fact]
    public void TheSmallBallManagerPlaysSmallBall_AndTheSluggerNeverDoes()
    {
        var smallBall = SmallBallPerGame("smallball", 40);
        var analyst = SmallBallPerGame("analyst", 40);
        var slugger = SmallBallPerGame("slugger", 40);

        Assert.True(smallBall > analyst, $"small ball {smallBall:F2} vs analyst {analyst:F2} per game");
        Assert.True(smallBall > 1.0, $"small ball {smallBall:F2} per game");
        Assert.Equal(0, slugger);   // never bunts, steals or runs on the pitch
    }

    [Fact]
    public void TheTinkererChangesPitchersOnSmallerEdgesThanTheSlugger()
    {
        // One slightly tired starter (85 pitches) and one warm reliever, whose quality is stepped from excellent to awful.
        // The Tinkerer goes to the reliever on a smaller edge than the Analyst, and the Analyst than the Slugger, who
        // trusts his starter.
        bool Changes(string persona, double relieverEra)
        {
            var state = NewGame(ManagerCatalog.Default, null, null).State!;
            state.CurrentInning = 8;
            state.HomePitcher.PitchCount = 85;
            state.HomePitcher.BattersFaced = 24;
            var reliever = state.HomeBullpen.First(w => w.Player.Role != PitcherRole.Closer);
            foreach (var w in state.HomeBullpen) w.IsWarming = false;
            reliever.IsWarming = true;
            reliever.BattersFacedWarming = 5;
            reliever.Player.PitchingOverall!.ERA = relieverEra;
            reliever.Player.PitchingVsLeft = null;
            reliever.Player.PitchingVsRight = null;
            return ManagerCatalog.Default.Create(persona, new Random(1))
                .DecidePitching(state, visitor: false).ChangeToId == reliever.Player.Id;
        }

        var ladder = Enumerable.Range(0, 40).Select(i => 0.5 + i * 0.25).ToList();   // ERA 0.5 .. 10.25
        var slugger = ladder.Where(era => Changes("slugger", era)).ToList();
        var analyst = ladder.Where(era => Changes("analyst", era)).ToList();
        var tinkerer = ladder.Where(era => Changes("tinkerer", era)).ToList();

        Assert.True(analyst.Count >= slugger.Count, $"analyst {analyst.Count} vs slugger {slugger.Count} ERA steps");
        Assert.True(tinkerer.Count > analyst.Count, $"tinkerer {tinkerer.Count} vs analyst {analyst.Count} ERA steps");
        Assert.Subset(analyst.ToHashSet(), slugger.ToHashSet());   // whoever goes with the slugger goes with the analyst
        Assert.Subset(tinkerer.ToHashSet(), analyst.ToHashSet());
    }

    [Fact]
    public void TheOldSchoolManagerWalksTheBigBat_AndTheAnalystDoesNot()
    {
        var state = NewGame(ManagerCatalog.Default, null, null).State!;
        state.CurrentInning = 3;
        state.IsTopOfInning = false;                         // the visitors field; the home team's batter is up
        var slugger = state.CurrentBatter;
        slugger.VsLeft = null;
        slugger.VsRight = null;
        slugger.Overall = new BatterStats { AtBats = 500, Hits = 170, HomeRuns = 45, Walks = 90, Strikeouts = 80 };
        state.RunnerOnSecond = new BaseRunner { Player = state.LineupPlayers(false).Skip(5).First().Player };

        int oldWalks = Enumerable.Range(0, 200).Count(seed =>
            ManagerCatalog.Default.Create("oldschool", new Random(seed)).MakeDefensiveDecision(state).IntentionalWalk);
        int analystWalks = Enumerable.Range(0, 200).Count(seed =>
            ManagerCatalog.Default.Create("analyst", new Random(seed)).MakeDefensiveDecision(state).IntentionalWalk);

        Assert.InRange(oldWalks, 60, 140);   // the book: a power hitter, first base open, about half the time
        Assert.Equal(0, analystWalks);       // in the third inning the numbers never favor it
    }
}
