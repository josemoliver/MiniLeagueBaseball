using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;
using MiniLeague.Web.Components.Game;
using MiniLeague.Web.Pages;
using MiniLeague.Web.Services;
using MudBlazor;
using MudBlazor.Services;

namespace MiniLeague.Web.Tests;

/// <summary>Renders the real Razor components (no browser) to catch runtime binding errors.</summary>
public class GameRoomTests : BunitContext, IAsyncLifetime
{
    private static readonly string DataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(20);  // the computer pauses between plays (3s each, more while a song plays)

    /// <summary>Serves wwwroot/data files so TeamRepository works without a web server.</summary>
    private sealed class FileHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Path.Combine(DataDir, request.RequestUri!.AbsolutePath.TrimStart('/')
                .Replace("data/", "", StringComparison.Ordinal));
            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage { Content = new StringContent(File.ReadAllText(path)) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    /// <summary>Stands in for browser storage; keeps the JSON so tests see exactly what would be saved.</summary>
    private sealed class MemorySaveStore : IGameSaveStore
    {
        public string? Json { get; set; }

        public Task SaveAsync(GameState state)
        {
            Json = state.Status == GameStatus.Final ? null : GameSnapshots.ToJson(GameSnapshots.Capture(state));
            return Task.CompletedTask;
        }

        public Task<GameSnapshot?> LoadAsync() => Task.FromResult(Json is null ? null : GameSnapshots.FromJson(Json));
        public Task ClearAsync() { Json = null; return Task.CompletedTask; }
        public GameSnapshot? Snapshot => Json is null ? null : GameSnapshots.FromJson(Json);
    }

    /// <summary>Dice that never trigger a random event, so UI tests are repeatable.</summary>
    private sealed class QuietDice : Random
    {
        public override double NextDouble() => 0.999;
    }

    private readonly MemorySaveStore _store = new();
    private IRenderedComponent<MudPopoverProvider> _popovers = null!;

    public GameRoomTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // These tests cover the game itself with the Classic field. (Detailed is the default for a visitor who has
        // not chosen, and DetailedViewTests cover it.)
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-fieldview").SetResult("classic");
        Services.AddMudServices();
        Services.AddSingleton(new HttpClient(new FileHandler()) { BaseAddress = new Uri("http://localhost/") });
        Services.AddScoped<TeamRepository>();
        Services.AddScoped<SoundService>();
        Services.AddScoped<CastService>();
        Services.AddScoped<FieldViewPreferences>();
        Services.AddSingleton<IGameSaveStore>(_store);
        Services.AddSingleton<Random>(new QuietDice());
        Services.AddScoped<ExportService>();
        Services.AddScoped(_ => new AtBatResolver(new Random(7)));
        Services.AddScoped(_ => new ComputerManager(new Random(7)));
    }

    // MudBlazor services only implement IAsyncDisposable, so dispose asynchronously.
    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private IRenderedComponent<MudDialogProvider> RenderProviders()
    {
        _popovers = Render<MudPopoverProvider>();
        return Render<MudDialogProvider>();
    }

    private static void Click(IRenderedComponent<IComponent> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains(text)).Click();

    private IRenderedComponent<GameRoom> OpenGameRoom()
    {
        var room = Render<GameRoom>();
        room.WaitForState(() => room.Markup.Contains("PLAY BALL") || room.Markup.Contains("RESUME"));
        return room;
    }

    private IRenderedComponent<GameRoom> StartedGame(string? manage = null)
    {
        var room = OpenGameRoom();
        if (manage is not null) ChooseManager(room, manage);
        Click(room, "PLAY BALL");
        return room;
    }

    // The third select on the setup card is "You manage".
    private void ChooseManager(IRenderedComponent<GameRoom> room, string option)
    {
        room.FindAll(".mud-select .mud-input-control")[2].MouseDown();
        _popovers.WaitForState(() => _popovers.FindAll(".mud-list-item").Count > 0);
        _popovers.FindAll(".mud-list-item").First(i => i.TextContent.Contains(option)).Click();
    }

    // A game saved mid-way, built with the real engine.
    private static GameSnapshot SnapshotOf(Action<GameState>? adjust = null,
        ManagerControl visitor = ManagerControl.Human, ManagerControl home = ManagerControl.Computer)
    {
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));
        var game = new GameService(new AtBatResolver(new Random(3)), new ComputerManager(new Random(3)));
        game.StartGame(Load("1927-nyy.json"), Load("1955-bro.json"), visitor, home);
        adjust?.Invoke(game.State!);
        return GameSnapshots.Capture(game.State!);
    }

    private static int Count(string text, string part) =>
        (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    public void StartingAGame_SendsItToTheTv()
    {
        RenderProviders();
        StartedGame("Neither");

        Assert.NotEmpty(JSInterop.Invocations["mlbCast.send"]);
    }

    [Fact]
    public async Task WhileATvShowsTheField_ThisPageGivesUpTheSpace()
    {
        RenderProviders();
        var room = StartedGame("Neither");
        Assert.NotEmpty(room.FindAll("svg.field-svg"));
        Assert.Empty(room.FindAll("[data-testid=casting-note]"));
        var cast = Services.GetRequiredService<CastService>();

        await room.InvokeAsync(() => cast.OnCastChanged(available: true, connected: true));
        Assert.Contains("cast-away", room.Find("[data-testid=field-region]").ClassName);
        Assert.Empty(room.FindAll("svg.field-svg"));
        Assert.Contains("on the TV", room.Find("[data-testid=casting-note]").TextContent);

        await room.InvokeAsync(() => cast.OnCastChanged(available: true, connected: false));
        Assert.NotEmpty(room.FindAll("svg.field-svg"));
        Assert.Empty(room.FindAll("[data-testid=casting-note]"));
    }

    // ── Setup and basic flow ─────────────────────────────────────────────────

    [Fact]
    public void GameRoom_LoadsTeamsStartsAGameAndSavesIt()
    {
        RenderProviders();
        var room = StartedGame();

        Assert.Contains("NYY LINEUP", room.Markup);
        Assert.Contains("BRO LINEUP", room.Markup);
        Assert.Contains("SUBS", room.Markup);
        Assert.Contains("Bullpen:", room.Markup);
        Assert.NotNull(_store.Snapshot);
        Assert.Equal(GameStatus.InProgress, _store.Snapshot!.Status);
    }

    [Fact]
    public void LineupEditor_ShowsAllNineSlotsAndBench()
    {
        var providers = RenderProviders();
        var team = TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", "1927-nyy.json")));
        var dialogs = Services.GetRequiredService<IDialogService>();

        providers.InvokeAsync(() => dialogs.ShowAsync<LineupEditorDialog>("Lineup",
            new DialogParameters<LineupEditorDialog>
            {
                { x => x.Team, team },
                { x => x.Current, LineupRules.Default(team) },
            }));

        providers.WaitForState(() => providers.Markup.Contains("Lineup"));
        Assert.Contains("1927 New York Yankees", providers.Markup);
        Assert.Equal(18, providers.FindAll(".mud-input-control").Count);  // player + position select per row
        Assert.Contains("Bench:", providers.Markup);
        Assert.DoesNotContain("mud-alert", providers.Markup);  // default lineup is valid
    }

    // ── Substitutions ────────────────────────────────────────────────────────

    [Fact]
    public void SubstitutionDialog_OpensFromTheGameAndListsTheTabs()
    {
        var providers = RenderProviders();
        var room = StartedGame();

        Click(room, "SUBS");

        providers.WaitForState(() => providers.Markup.Contains("Substitutions"));
        foreach (var tab in new[] { "Pinch Hit", "Pinch Run", "Replace Fielder", "Swap Positions", "Pitching" })
            Assert.Contains(tab, providers.Markup);
        Assert.Contains("At bat:", providers.Markup);
    }

    [Fact]
    public void SubstitutionDialog_PinchHitSwapsTheBatterAndUpdatesTheLineupCard()
    {
        var providers = RenderProviders();
        var room = StartedGame();
        string batterBefore = room.Find("[data-testid=at-bat-name]").TextContent;

        Click(room, "SUBS");
        providers.WaitForState(() => providers.Markup.Contains("Pinch hitter"));

        // Open the "Pinch hitter" select and choose the first bench player.
        providers.Find(".mud-select .mud-input-control").MouseDown();
        _popovers.WaitForState(() => _popovers.FindAll(".mud-list-item").Count > 0);
        _popovers.FindAll(".mud-list-item")[0].Click();
        Click(providers, "Pinch hit");

        providers.WaitForState(() => providers.Markup.Contains("Pinch hitter in."));
        Click(providers, "Done");
        room.WaitForAssertion(() =>
        {
            Assert.NotEqual(batterBefore, room.Find("[data-testid=at-bat-name]").TextContent);
            Assert.Contains("Pinch hitter", room.Markup);  // logged in the play-by-play
        });
        Assert.Contains("Pinch hitter", _store.Json);       // and saved
    }

    // ── Bullpen ──────────────────────────────────────────────────────────────

    [Fact]
    public void PitchingTab_WarmsUpARelieverAndBringsHimIn()
    {
        var providers = RenderProviders();
        var room = StartedGame();
        string visitorPen() => room.FindAll("[data-testid=bullpen]")[0].TextContent;
        Assert.Equal(3, Count(visitorPen(), "(Resting)"));

        Click(room, "SUBS");
        providers.WaitForState(() => providers.Markup.Contains("Substitutions"));
        providers.FindAll(".mud-tab").First(t => t.TextContent.Contains("Pitching")).Click();
        providers.WaitForState(() => providers.Markup.Contains("On the mound"));

        Click(providers, "Warm up");
        providers.WaitForState(() => providers.Markup.Contains("is warming up."));
        Click(providers, "Done");
        room.WaitForAssertion(() => Assert.Contains("Warming (0/4)", visitorPen()));

        // Bring the warming reliever in (the first one listed).
        Click(room, "SUBS");
        providers.WaitForState(() => providers.Markup.Contains("Substitutions"));
        providers.FindAll(".mud-tab").First(t => t.TextContent.Contains("Pitching")).Click();
        providers.WaitForState(() => providers.Markup.Contains("On the mound"));
        Click(providers, "Bring in");
        providers.WaitForState(() => providers.Markup.Contains("is in."));
        Click(providers, "Done");

        room.WaitForAssertion(() =>
        {
            Assert.Contains("relieves", room.Markup);                  // play-by-play entry
            Assert.Equal(2, Count(visitorPen(), "("));                  // he left the bullpen
            Assert.DoesNotContain("Warming", visitorPen());
        });
        Assert.Contains("relieves", _store.Json);
        Assert.Contains(JSInterop.Invocations["mlbSound.play"], i => (string?)i.Arguments[0] == "entrance");   // Hail to the Chief
    }

    // ── Who manages ──────────────────────────────────────────────────────────

    [Fact]
    public void ManagingTheHomeTeam_AsksForDefensiveDecisionsWhileTheComputerBats()
    {
        RenderProviders();
        var room = StartedGame(manage: "Home team");

        // The computer visitors bat first; then the human is asked how to defend.
        room.WaitForState(() => room.Markup.Contains("DEFENSIVE STRATEGY"), Slow);
        Assert.DoesNotContain("OFFENSIVE STRATEGY", room.Markup);
        Assert.Contains("IBB", room.Markup);

        int playsBefore = _store.Snapshot!.PlayByPlay.Count;
        Click(room, "PITCH");

        room.WaitForAssertion(() =>
            Assert.True(_store.Snapshot!.PlayByPlay.Count > playsBefore), Slow);
    }

    [Fact]
    public void ManagingBothTeams_ShowsBothPanelsAndLetsTheDialogSwitchSides()
    {
        var providers = RenderProviders();
        var room = StartedGame(manage: "Both teams");

        Assert.Contains("OFFENSIVE STRATEGY", room.Markup);
        Assert.Contains("DEFENSIVE STRATEGY", room.Markup);

        Click(room, "SUBS");
        providers.WaitForState(() => providers.Markup.Contains("Substitutions"));
        Click(providers, "BRO");  // switch to managing the home team
        providers.WaitForState(() => providers.Markup.Contains("1955 Brooklyn Dodgers"));
        providers.WaitForAssertion(() => Assert.Contains("Your team is in the field", providers.Markup));
    }

    [Fact]
    public void WatchingTheComputer_NeverAsksForADecision()
    {
        RenderProviders();
        var room = StartedGame(manage: "Neither");

        room.WaitForAssertion(() => Assert.True(_store.Snapshot!.PlayByPlay.Count >= 2), Slow);
        Assert.DoesNotContain("STRATEGY", room.Markup);
    }

    // ── Save and resume ──────────────────────────────────────────────────────

    [Fact]
    public void ASavedGame_OffersResumeAndRestoresTheGame()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            s.CurrentInning = 6; s.IsTopOfInning = true; s.Outs = 1;
            s.AddRuns(visitor: true, inning: 3, runs: 2);
            s.AddRuns(visitor: false, inning: 5, runs: 1);
        }));
        RenderProviders();
        var room = OpenGameRoom();

        var card = room.Find("[data-testid=resume-card]");
        Assert.Contains("NYY 2, BRO 1", card.TextContent);
        Assert.Contains("▲6", card.TextContent);

        Click(room, "RESUME");

        room.WaitForState(() => room.Markup.Contains("NYY LINEUP"));
        Assert.Contains("▲ 6", room.Markup);
        Assert.DoesNotContain("resume-card", room.Markup);
    }

    [Fact]
    public void DiscardingASavedGame_ClearsIt()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf());
        RenderProviders();
        var room = OpenGameRoom();

        Click(room, "DISCARD");

        room.WaitForAssertion(() => Assert.DoesNotContain("resume-card", room.Markup));
        Assert.Null(_store.Json);
    }

    [Fact]
    public void ASaveThatNoLongerMatchesTheRosters_IsRejectedWithAMessage()
    {
        var snapshot = SnapshotOf();
        snapshot.VisitorLineup[0] = 99999;  // a player that is not on the team
        snapshot.VisitorPositions[99999] = MiniLeague.Engine.Models.Position.C;
        _store.Json = GameSnapshots.ToJson(snapshot);
        RenderProviders();
        var room = OpenGameRoom();

        Click(room, "RESUME");

        room.WaitForAssertion(() => Assert.Contains("has changed since this game was saved", room.Markup));
        Assert.Null(_store.Json);
    }

    // ── Scoreboard ───────────────────────────────────────────────────────────

    [Fact]
    public void ExtraInnings_AddColumnsToTheLineScore()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            s.CurrentInning = 11; s.IsTopOfInning = true;
            s.AddRuns(visitor: true, inning: 10, runs: 1);
        }));
        RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");

        room.WaitForState(() => room.FindAll("[data-testid=linescore]").Count > 0);
        var line = room.Find("[data-testid=linescore]");
        Assert.Equal(3 * 11, line.QuerySelectorAll(".ls-inn").Length);  // header + two teams, 11 innings
        Assert.Contains("repeat(11", line.GetAttribute("style"));
    }

    [Fact]
    public void WhenTheHomeTeamDoesNotBatInTheNinth_TheLineScoreShowsX()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            s.CurrentInning = 9; s.IsTopOfInning = true; s.Status = GameStatus.Final;
            s.AddRuns(visitor: false, inning: 2, runs: 3);
        }));
        RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");

        room.WaitForState(() => room.FindAll("[data-testid=linescore]").Count > 0);
        var cells = room.Find("[data-testid=linescore]").QuerySelectorAll(".ls-inn")
            .Select(c => c.TextContent.Trim()).ToList();
        Assert.Equal("X", cells[^1]);  // last cell is the home team's 9th
    }

    [Fact]
    public void ARunFlashesTheTotalAndTheInningItCameIn()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            s.CurrentInning = 3; s.IsTopOfInning = true;
            s.AddRuns(visitor: true, inning: 3, runs: 2);
        }));
        RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");

        room.WaitForState(() => room.FindAll("[data-testid=linescore]").Count > 0);
        var flashing = room.Find("[data-testid=linescore]").QuerySelectorAll(".ls-flash").Select(c => c.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "2", "2" }, flashing);   // this inning's cell, then the visitors' total: nothing for the home team's 0
    }

    [Fact]
    public void AScoreboardWithNoRunsFlashesNothing()
    {
        RenderProviders();
        var room = StartedGame();

        Assert.Empty(room.Find("[data-testid=linescore]").QuerySelectorAll(".ls-flash"));
    }

    // ── Ballparks ────────────────────────────────────────────────────────────

    [Fact]
    public void TheGameIsPlayedInTheHomeTeamsPark_ByDefault_AndTheFieldShowsItsFences()
    {
        RenderProviders();
        var room = StartedGame();   // 1927 Yankees at the 1955 Dodgers

        Assert.Contains("Ebbets Field", room.Find("[data-testid=park-name]").TextContent);
        string field = room.Find(".field-svg").InnerHtml;
        foreach (var distance in new[] { ">348<", ">393<", ">297<" })   // left field, center, right field
            Assert.Contains(distance, field);
    }

    [Fact]
    public void AnyBallparkCanBeChosen_AndItsDimensionsReplaceTheHomeParks()
    {
        RenderProviders();
        var room = OpenGameRoom();

        room.FindAll(".mud-select .mud-input-control")[3].MouseDown();   // the Ballpark select
        _popovers.WaitForState(() => _popovers.FindAll(".mud-list-item").Count > 3);
        _popovers.FindAll(".mud-list-item").First(i => i.TextContent.Trim() == "Fenway Park").Click();
        Click(room, "PLAY BALL");

        Assert.Contains("Fenway Park", room.Find("[data-testid=park-name]").TextContent);
        string field = room.Find(".field-svg").InnerHtml;
        Assert.Contains(">302<", field);   // short right-field line
        Assert.Contains(">315<", field);
        Assert.DoesNotContain(">297<", field);
        Assert.Equal("fenway-1975", _store.Snapshot!.StadiumId);
    }

    [Fact]
    public void TheSetupScreenOffersRandomEventsAndLetsThemBeSwitchedOff()
    {
        RenderProviders();
        var room = OpenGameRoom();
        Assert.Contains("Random events", room.Markup);

        room.Find(".mud-switch input").Change(false);
        Click(room, "PLAY BALL");

        Assert.Equal(GameOptions.None, _store.Snapshot!.Options);
    }

    [Fact]
    public void RandomEventsAreOnByDefault()
    {
        RenderProviders();
        StartedGame();

        Assert.Equal(GameOptions.Standard, _store.Snapshot!.Options);
    }

    // ── Box score ────────────────────────────────────────────────────────────

    [Fact]
    public void TheBoxScoreOpensDuringTheGame_WithBothTeamsHittersAndPitchers()
    {
        var providers = RenderProviders();
        var room = StartedGame();

        Click(room, "Box score");

        providers.WaitForState(() => providers.FindAll("[data-testid=box-score]").Count > 0);
        var box = providers.Find("[data-testid=box-score]");
        Assert.Contains("1927 New York Yankees 0, 1955 Brooklyn Dodgers 0", box.TextContent);
        Assert.Contains("Ebbets Field", box.TextContent);
        Assert.Equal(5, box.QuerySelectorAll("table.box-table").Length);   // line score + two batting + two pitching
        Assert.Contains("1927 NEW YORK YANKEES", box.TextContent);
        Assert.Contains("1955 BROOKLYN DODGERS", box.TextContent);
        Assert.Contains("NYY PITCHING", box.TextContent);
        Assert.Contains("B. Ruth", box.TextContent);
        Assert.Contains("Totals", box.TextContent);
    }

    [Fact]
    public void TheBoxScoreCanBeCopiedAsText_AndPrinted()
    {
        var providers = RenderProviders();
        JSInterop.Setup<bool>("mlbExport.copy", _ => true).SetResult(true);
        JSInterop.SetupVoid("mlbExport.print").SetVoidResult();
        var room = StartedGame();
        Click(room, "Box score");
        providers.WaitForState(() => providers.Markup.Contains("Copy text"));

        Click(providers, "Copy text");
        providers.WaitForAssertion(() => Assert.Contains("Copied to the clipboard", providers.Find("[data-testid=copy-status]").TextContent));
        var copied = (string)JSInterop.Invocations["mlbExport.copy"].Single().Arguments[0]!;
        Assert.Contains("AB  R  H RBI BB SO", copied);
        Assert.Contains("NYY PITCHING", copied);

        Click(providers, "Print");
        JSInterop.VerifyInvoke("mlbExport.print");
    }

    [Fact]
    public void TheFinalPanel_ShowsWinLossAndSave_AndRainCalledGames()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            s.CurrentInning = 7; s.IsTopOfInning = false; s.Status = GameStatus.Final; s.EndedByRain = true;
            s.AddRuns(visitor: true, inning: 1, runs: 3);
            s.Box.Visitor.Pitchers[0].Decision = "W";
            s.Box.Home.Pitchers[0].Decision = "L";
        }));
        RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");

        room.WaitForState(() => room.Markup.Contains("Called because of rain"));
        var decisions = room.Find("[data-testid=decisions]").TextContent;
        Assert.Contains("Win: W. Hoyt", decisions);
        Assert.Contains("Loss:", decisions);
        Assert.Contains("FINAL (rain)", room.Markup);
    }

    // ── Injuries ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnInjuredHitterOnAHumanTeam_PromptsForAReplacementWhoThenTakesHisPlace()
    {
        int hurtId = 0;
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            hurtId = s.VisitorLineup[3];
            s.PendingInjury = new InjuryNotice(true, hurtId, false);
        }));
        var providers = RenderProviders();
        var room = OpenGameRoom();

        Click(room, "RESUME");

        providers.WaitForState(() => providers.Markup.Contains("is injured and has to leave the game"));
        Assert.Contains("Who takes his place at", providers.Markup);
        Assert.Contains("range", providers.Markup);   // fielding info for each candidate

        providers.FindAll("button").First(b => b.TextContent.Contains("HR")).Click();   // the first bench player

        room.WaitForAssertion(() => Assert.DoesNotContain("injury-prompt", room.Markup));
        Assert.DoesNotContain(hurtId, _store.Snapshot!.VisitorLineup);
        Assert.Null(_store.Snapshot.PendingInjury);
    }

    [Fact]
    public void AnInjuredPitcherOnAHumanTeam_OffersTheBullpen()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            s.PendingInjury = new InjuryNotice(true, s.VisitorPitcher.Player.Id, true);
            s.IsTopOfInning = false;   // the visitors' pitcher is the one on the mound
        }, visitor: ManagerControl.Human, home: ManagerControl.Computer));
        var providers = RenderProviders();
        var room = OpenGameRoom();

        Click(room, "RESUME");

        providers.WaitForState(() => providers.Markup.Contains("Who comes in to pitch?"));
        Assert.Contains("ERA", providers.Markup);
        providers.FindAll("button").First(b => b.TextContent.Contains("ERA")).Click();
        room.WaitForAssertion(() => Assert.Null(_store.Snapshot!.PendingInjury));
        Assert.Equal(2, _store.Snapshot!.Box.Visitor.Pitchers.Count);
    }

    // ── Fielding in the lineup screens ───────────────────────────────────────

    [Fact]
    public void ALineupCard_FlagsAPlayerWhoIsOutOfPosition()
    {
        _store.Json = GameSnapshots.ToJson(SnapshotOf(s =>
        {
            // Swap the catcher and the shortstop: neither has played the other's position.
            var positions = s.VisitorPositions;
            int c = positions.First(kv => kv.Value == MiniLeague.Engine.Models.Position.C).Key;
            int ss = positions.First(kv => kv.Value == MiniLeague.Engine.Models.Position.Short).Key;
            (positions[c], positions[ss]) = (positions[ss], positions[c]);
        }));
        RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");

        room.WaitForState(() => room.Markup.Contains("NYY LINEUP"));
        Assert.Contains("lineup-warn", room.Markup);
        Assert.Contains("⚠", room.Markup);
    }

    [Fact]
    public void TheLineupEditor_ShowsEachPlayersGloveAtHisPosition()
    {
        var providers = RenderProviders();
        var team = TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", "1975-bos.json")));
        var dialogs = Services.GetRequiredService<IDialogService>();

        providers.InvokeAsync(() => dialogs.ShowAsync<LineupEditorDialog>("Lineup",
            new DialogParameters<LineupEditorDialog>
            {
                { x => x.Team, team },
                { x => x.Current, LineupRules.Default(team) },
            }));

        providers.WaitForState(() => providers.Markup.Contains("range"));
        Assert.Contains("glove", providers.Markup);
        Assert.Contains("designated hitter", providers.Markup);   // the DH does not field
    }

    // ── Startup page ─────────────────────────────────────────────────────────

    [Fact]
    public void TheStartupPage_ShowsTheArtworkAboveThePlayBallButton()
    {
        var page = Render<MiniLeague.Web.Pages.Index>();

        var art = page.Find("img.start-art");
        Assert.EndsWith("images/minileague-baseball.webp", art.GetAttribute("src"));
        Assert.Contains("MiniLeague Baseball", art.GetAttribute("alt"));
        Assert.Equal("MiniLeague Baseball", page.Find("h1").TextContent);   // kept for screen readers

        var play = page.FindAll("a").First(a => a.TextContent.Contains("PLAY BALL"));
        Assert.Equal("game", play.GetAttribute("href"));   // relative, so it works under a sub-path like /MiniLeagueBaseball/
    }

    [Fact]
    public void TheStartupPage_AsksForTheOrganThemeOnce_AndPlayBallStepsAsideFromIt()
    {
        var page = Render<MiniLeague.Web.Pages.Index>();

        Assert.Single(JSInterop.Invocations["mlbSound.playTheme"]);

        page.Render();   // a re-render must not ask again
        Assert.Single(JSInterop.Invocations["mlbSound.playTheme"]);

        var play = page.FindAll("a").First(a => a.TextContent.Contains("PLAY BALL"));
        Assert.NotNull(play.GetAttribute("data-no-theme"));   // the page script skips the theme for this click
    }

    [Fact]
    public async Task IfTheBrowserCannotPlayAudio_TheStartupPageStillWorks()
    {
        JSInterop.SetupVoid("mlbSound.playTheme").SetException(new JSException("no audio"));
        var page = Render<MiniLeague.Web.Pages.Index>();

        Assert.Contains("PLAY BALL", page.Markup);
        await Task.CompletedTask;
    }

    [Fact]
    public void TheArtworkFileShipsWithTheApp()
    {
        var file = Path.Combine(DataDir, "..", "images", "minileague-baseball.webp");

        Assert.True(File.Exists(file));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file), 0, 4));   // a real WebP
    }

    // ── Theme ────────────────────────────────────────────────────────────────

    // WCAG relative luminance, to check that the white menubar text stays readable on the blue.
    private static double Luminance(string hex)
    {
        double Channel(int shift)
        {
            double c = ((Convert.ToInt32(hex.TrimStart('#'), 16) >> shift) & 0xFF) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(16) + 0.7152 * Channel(8) + 0.0722 * Channel(0);
    }

    [Theory]
    [InlineData(AppTheme.ArtworkBlue)]
    [InlineData(AppTheme.ArtworkBlueDark)]
    public void TheMenubarBlue_IsBlue_AndWhiteTextOnItIsReadable(string hex)
    {
        int rgb = Convert.ToInt32(hex.TrimStart('#'), 16);
        int r = rgb >> 16, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        Assert.True(b > r + 60 && b > g + 30, "should be clearly blue");

        double contrast = (Luminance("#ffffff") + 0.05) / (Luminance(hex) + 0.05);
        Assert.True(contrast >= 7, $"contrast {contrast:F1}:1 is too low for white text");   // AAA for normal text
    }

    [Fact]
    public void TheMenubarUsesTheArtworkBlue_InLightAndDarkMode()
    {
        Assert.Equal(AppTheme.ArtworkBlue, AppTheme.Theme.PaletteLight.AppbarBackground.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToLowerInvariant().Substring(0, 7));
        Assert.Equal(AppTheme.ArtworkBlueDark, AppTheme.Theme.PaletteDark.AppbarBackground.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToLowerInvariant().Substring(0, 7));
    }

    [Fact]
    public void TheThemeIsAppliedToThePage()
    {
        var provider = Render<MudThemeProvider>(p => p.Add(x => x.Theme, AppTheme.Theme));

        Assert.Contains("--mud-palette-appbar-background", provider.Markup);
        Assert.Contains("11,78,162", provider.Markup.Replace(" ", ""));   // #0b4ea2 as rgb
    }

    // ── Starting pitchers ────────────────────────────────────────────────────

    [Fact]
    public void TheSetupScreen_OffersEachTeamsPitchers_WithTheAceSelectedByDefault()
    {
        RenderProviders();
        var room = OpenGameRoom();

        Assert.Contains("W. Hoyt — 22-7 · 2.63 ERA", room.Markup);   // the 1927 Yankees ace, selected for the visitors
        Assert.Contains("Home starting pitcher", room.Markup);
    }

    [Fact]
    public void AnyPitcherCanBeChosenToStart_AndTheGameUsesHim()
    {
        var providers = RenderProviders();
        var room = OpenGameRoom();

        // the two starter selects follow the ballpark select: visitor first
        room.FindAll(".mud-select .mud-input-control")[4].MouseDown();
        _popovers.WaitForState(() => _popovers.FindAll(".mud-list-item").Count > 3);
        _popovers.FindAll(".mud-list-item").First(i => i.TextContent.Contains("U. Shocker")).Click();
        Click(room, "PLAY BALL");

        Assert.Equal("U. Shocker", _store.Snapshot!.Box.Visitor.Pitchers[0].Name);
        Assert.Contains("Shocker", room.Markup);
        Assert.Contains("U. Shocker", room.FindAll("b").Select(b => b.TextContent));   // the visitors' pitcher on the lineup card
        Assert.NotNull(providers);
    }

    [Fact]
    public void TheListIncludesTheBullpen_WithTheirRoles()
    {
        RenderProviders();
        var room = OpenGameRoom();

        room.FindAll(".mud-select .mud-input-control")[4].MouseDown();
        _popovers.WaitForState(() => _popovers.FindAll(".mud-list-item").Count > 3);

        var items = _popovers.FindAll(".mud-list-item").Select(i => i.TextContent).ToList();
        Assert.Contains(items, t => t.Contains("(Closer)"));
        Assert.Contains(items, t => t.Contains("(Reliever)"));
    }

    // ── Win probability and the recap ────────────────────────────────────────

    // A real game from the engine, saved partway (or to the end) and handed back to the page.
    private static GameSnapshot PlayedGame(int plays, int seed = 4)
    {
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));
        var rng = new Random(seed);
        var game = new GameService(new AtBatResolver(rng), new ComputerManager(rng), rng);
        game.StartGame(Load("1927-nyy.json"), Load("1955-bro.json"), ManagerControl.Computer, ManagerControl.Computer);
        for (int i = 0; i < plays && game.State!.Status == GameStatus.InProgress; i++) game.ExecuteComputerTurn();
        while (plays == int.MaxValue && game.State!.Status == GameStatus.InProgress) game.ExecuteComputerTurn();

        var snapshot = GameSnapshots.Capture(game.State!);
        snapshot.VisitorControl = ManagerControl.Human;   // so the page waits for the person
        return snapshot;
    }

    [Fact]
    public void TheScoreboardShowsWhoIsFavoured_WhileTheGameIsOn_AndNotAfter()
    {
        _store.Json = GameSnapshots.ToJson(PlayedGame(25));
        RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");

        room.WaitForState(() => room.FindAll("[data-testid=win-prob]").Count > 0);
        Assert.Matches(@"Win probability: (NYY|BRO) \d+%", room.Find("[data-testid=win-prob]").TextContent);

        _store.Json = GameSnapshots.ToJson(PlayedGame(int.MaxValue));
        var done = OpenGameRoom();
        Click(done, "RESUME");
        done.WaitForState(() => done.Markup.Contains("FINAL"));
        Assert.Empty(done.FindAll("[data-testid=win-prob]"));
    }

    [Fact]
    public void TheRecapOpensMidGame_WithTheChartAndTheStoryThusFar()
    {
        var snapshot = PlayedGame(30);
        _store.Json = GameSnapshots.ToJson(snapshot);
        var providers = RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");
        room.WaitForState(() => room.Markup.Contains("NYY LINEUP"));

        Click(room, "Recap");

        providers.WaitForState(() => providers.FindAll("[data-testid=recap]").Count > 0);
        Assert.Contains(" after ", providers.Find("[data-testid=recap-headline]").TextContent);   // not over yet
        Assert.Equal(snapshot.Recap.Count + 1, int.Parse(providers.Find("[data-testid=wp-chart]").GetAttribute("data-points")!));
        Assert.Contains("WIN PROBABILITY", providers.Markup);
    }

    [Fact]
    public void AFinishedGamesRecap_HasHeadlineStoryHighlightsAndPerformers()
    {
        _store.Json = GameSnapshots.ToJson(PlayedGame(int.MaxValue));
        var providers = RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");
        room.WaitForState(() => room.Markup.Contains("Game recap"));

        Click(room, "Game recap");

        providers.WaitForState(() => providers.FindAll("[data-testid=recap-headline]").Count > 0);
        Assert.Contains(" beat the ", providers.Find("[data-testid=recap-headline]").TextContent);
        Assert.NotEmpty(providers.FindAll("[data-testid=recap-story] li"));
        Assert.NotEmpty(providers.FindAll("[data-testid=recap-highlights] li"));
        Assert.NotEmpty(providers.FindAll("[data-testid=recap-performers] li"));

        // every highlight is also marked on the chart, with its description as the tooltip
        Assert.Equal(providers.FindAll("[data-testid=recap-highlights] li").Count, providers.FindAll("[data-testid=wp-key-play]").Count);
        Assert.All(providers.FindAll("[data-testid=wp-key-play] title"), t => Assert.NotEmpty(t.TextContent));
    }

    [Fact]
    public void TheRecapCanBeCopiedAsText()
    {
        JSInterop.Setup<bool>("mlbExport.copy", _ => true).SetResult(true);
        _store.Json = GameSnapshots.ToJson(PlayedGame(int.MaxValue));
        var providers = RenderProviders();
        var room = OpenGameRoom();
        Click(room, "RESUME");
        room.WaitForState(() => room.Markup.Contains("Game recap"));
        Click(room, "Game recap");
        providers.WaitForState(() => providers.Markup.Contains("Copy recap"));

        Click(providers, "Copy recap");

        providers.WaitForAssertion(() => Assert.Contains("Copied", providers.Find("[data-testid=recap-status]").TextContent));
        var text = (string)JSInterop.Invocations["mlbExport.copy"].Single().Arguments[0]!;
        Assert.Contains(" beat the ", text);
        Assert.Contains("Highlights", text);
    }

    [Fact]
    public void WithNoPlaysYet_TheRecapSaysSoInsteadOfDrawingAnEmptyChart()
    {
        var providers = RenderProviders();
        var room = StartedGame();

        Click(room, "Recap");

        providers.WaitForState(() => providers.Markup.Contains("No plays yet"));
        Assert.Empty(providers.FindAll("[data-testid=wp-chart]"));
    }

    [Theory]
    [InlineData(AppTheme.ArtworkBlue, "#ffffff")]
    [InlineData(AppTheme.ArtworkRed, "#ffffff")]
    [InlineData("#1d2a44", AppTheme.ArtworkCream)]     // body text on the cream drawer
    [InlineData("#1d2a44", "#f3f5fa")]                  // body text on the page background
    [InlineData(AppTheme.ArtworkBlue, "#f3f5fa")]       // blue links and headings on the page background
    public void ThemeColors_AreReadableTogether(string foreground, string background)
    {
        double contrast = (Math.Max(Luminance(foreground), Luminance(background)) + 0.05)
                        / (Math.Min(Luminance(foreground), Luminance(background)) + 0.05);

        Assert.True(contrast >= 4.5, $"{foreground} on {background} is only {contrast:F1}:1");
    }

    [Fact]
    public void TheButtonsAndAccentsUseTheArtworkColors_NotTheDefaultPurple()
    {
        var light = AppTheme.Theme.PaletteLight;

        Assert.Equal(AppTheme.ArtworkBlue, light.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex)[..7].ToLowerInvariant());
        Assert.Equal(AppTheme.ArtworkRed, light.Secondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex)[..7].ToLowerInvariant());
        Assert.NotEqual("#594ae2", light.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex)[..7].ToLowerInvariant());
    }

    [Fact]
    public void TheThemeIsPresentInThePageStyles()
    {
        var provider = Render<MudThemeProvider>(p => p.Add(x => x.Theme, AppTheme.Theme));
        var css = provider.Markup.Replace(" ", "");

        Assert.Matches(@"--mud-palette-primary:rgba?\(11,78,162", css);     // the blue
        Assert.Matches(@"--mud-palette-secondary:rgba?\(200,50,58", css);   // the red
    }
}
