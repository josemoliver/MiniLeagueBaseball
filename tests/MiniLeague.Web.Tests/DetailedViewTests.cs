using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;
using MiniLeague.Web.Pages;
using MiniLeague.Web.Services;
using MudBlazor;
using MudBlazor.Services;

namespace MiniLeague.Web.Tests;

/// <summary>The Classic / Detailed field view choice, and the animated view's hold on the page during a play.</summary>
public class DetailedViewTests : BunitContext, IAsyncLifetime
{
    private static readonly string DataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot", "data"));

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

    private sealed class MemorySaveStore : IGameSaveStore
    {
        public string? Json { get; set; }
        public int Saves { get; private set; }

        public Task SaveAsync(GameState state)
        {
            Saves++;
            Json = state.Status == GameStatus.Final ? null : GameSnapshots.ToJson(GameSnapshots.Capture(state));
            return Task.CompletedTask;
        }

        public Task<GameSnapshot?> LoadAsync() => Task.FromResult(Json is null ? null : GameSnapshots.FromJson(Json));
        public Task ClearAsync() { Json = null; return Task.CompletedTask; }
    }

    private sealed class QuietDice : Random
    {
        public override double NextDouble() => 0.999;
    }

    private readonly MemorySaveStore _store = new();

    public DetailedViewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new HttpClient(new FileHandler()) { BaseAddress = new Uri("http://localhost/") });
        Services.AddScoped<TeamRepository>();
        Services.AddScoped<SoundService>();
        Services.AddScoped<CastService>();
        Services.AddScoped<FieldViewPreferences>();
        Services.AddSingleton<IGameSaveStore>(_store);
        Services.AddSingleton<Random>(new QuietDice());
        Services.AddScoped(_ => new AtBatResolver(new Random(7)));
        Services.AddScoped(_ => new ComputerManager(new Random(7)));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private void StoredView(string? view) =>
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-fieldview").SetResult(view);

    private IRenderedComponent<GameRoom> StartedGame(string? manage = null)
    {
        var popovers = Render<MudPopoverProvider>();
        var room = Render<GameRoom>();
        room.WaitForState(() => room.Markup.Contains("PLAY BALL"));
        if (manage is not null)
        {
            room.FindAll(".mud-select .mud-input-control")[2].MouseDown();
            popovers.WaitForState(() => popovers.FindAll(".mud-list-item").Count > 0);
            popovers.FindAll(".mud-list-item").First(i => i.TextContent.Contains(manage)).Click();
        }
        Click(room, "PLAY BALL");
        // The animated view introduces the game first: wait until that is over.
        if (room.Markup.Contains("detailed-field"))
            room.WaitForAssertion(() =>
            {
                Assert.NotEmpty(Calls("mlbAnimator.play"));
                Assert.Empty(room.FindAll("[data-testid=skip-play]"));
            });
        if (room.Markup.Contains("detailed-field")) Thread.Sleep(150);   // let the page finish with the introduction
        return room;
    }

    // The scenes around the plays (the introduction, a new half-inning, a new pitcher...) are played the same way as plays.
    private static readonly string[] SceneOutcomes = ["PlayBall", "HalfInning", "PitchingChange", "Substitutions", "MoundVisit", "WalkOff", "AlignmentChange"];

    [Fact]
    public void ChoosingAFieldPosition_PlaysItsSceneBeforeAPlay()
    {
        StoredView("detailed");
        var room = StartedGame(manage: "Home team");
        var alignment = room.FindAll(".decision-panel .mud-button-group-root").Last().QuerySelectorAll("button")[1];

        alignment.Click();

        room.WaitForAssertion(() => Assert.Contains(Calls("mlbAnimator.play"),
            call => ((string)call.Arguments[0]!).Contains("\"outcome\":\"AlignmentChange\"")));
        Assert.Empty(room.FindAll(".g-log .pbp-entry"));
    }

    private IReadOnlyList<JSRuntimeInvocation> PlayCalls() =>
        Calls("mlbAnimator.play").Where(c => !SceneOutcomes.Any(o => ((string)c.Arguments[0]!).Contains($"\"outcome\":\"{o}\""))).ToList();

    private static void Click(IRenderedComponent<IComponent> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains(text)).Click();

    private string AtBat(IRenderedComponent<GameRoom> room) => room.Find("[data-testid=at-bat-name]").TextContent;

    private IReadOnlyList<JSRuntimeInvocation> Calls(string name) => JSInterop.Invocations[name].ToList();

    // ── Classic is left alone, and stays available ───────────────────────────

    [Fact]
    public void Classic_WhenChosen_HasNoCanvasAndNoAnimatorCalls()
    {
        StoredView("classic");
        var room = StartedGame();

        Assert.Contains("field-svg", room.Markup);
        Assert.DoesNotContain("detailed-field", room.Markup);

        Click(room, "PITCH");
        room.WaitForAssertion(() => Assert.True(_store.Saves >= 2));

        Assert.Contains("field-svg", room.Markup);
        Assert.Empty(Calls("mlbAnimator.init"));
        Assert.Empty(Calls("mlbAnimator.show"));
        Assert.Empty(Calls("mlbAnimator.play"));
        // The usual sound for the play is still played, as before.
        Assert.NotEmpty(Calls("mlbSound.play"));
    }

    [Fact]
    public void InClassic_ThePageUpdatesStraightAwayAfterAPlay()
    {
        StoredView("classic");
        var room = StartedGame();
        string before = AtBat(room);

        Click(room, "PITCH");

        room.WaitForAssertion(() => Assert.NotEqual(before, AtBat(room)));
    }

    // ── Casting to a TV ──────────────────────────────────────────────────────

    [Fact]
    public async Task WhileATvShowsTheField_TheAnimatedFieldStaysMounted_ButOutOfTheWay()
    {
        var room = StartedGame();
        room.WaitForAssertion(() => Assert.Single(Calls("mlbAnimator.init")));

        await room.InvokeAsync(() => Services.GetRequiredService<CastService>().OnCastChanged(available: true, connected: true));

        // it still draws the plays that are passed on to the TV, but it takes no room on this screen
        Assert.Contains("cast-away", room.Find("[data-testid=field-region]").ClassName);
        Assert.Single(room.FindAll("[data-testid=detailed-field]"));
        Assert.Single(Calls("mlbAnimator.init"));

        await room.InvokeAsync(() => Services.GetRequiredService<CastService>().OnCastChanged(available: true, connected: false));
        Assert.DoesNotContain("cast-away", room.Find("[data-testid=field-region]").ClassName);
    }

    // ── Choosing the view ────────────────────────────────────────────────────

    [Fact]
    public void WithNothingChosenYet_TheAnimatedFieldIsShown()
    {
        var room = StartedGame();      // nothing in storage: a first visit

        Assert.Contains("detailed-field", room.Markup);
        Assert.DoesNotContain("field-svg", room.Markup);
        Assert.NotEmpty(room.FindAll("[data-testid=speed-normal]"));
        room.WaitForAssertion(() => Assert.Single(Calls("mlbAnimator.init")));
    }

    [Fact]
    public void AnUnreadableChoiceInStorage_ShowsTheAnimatedField()
    {
        StoredView("nonsense");
        var room = StartedGame();

        Assert.Contains("detailed-field", room.Markup);
    }

    [Fact]
    public void AClassicChoiceInStorage_IsKept()
    {
        StoredView("classic");
        var room = StartedGame();

        Assert.Contains("field-svg", room.Markup);
        Assert.DoesNotContain("detailed-field", room.Markup);
    }

    [Fact]
    public void ADetailedChoiceInStorage_ShowsTheCanvas_AndDrawsTheSituation()
    {
        StoredView("detailed");
        var room = StartedGame();

        Assert.Contains("detailed-field", room.Markup);
        Assert.DoesNotContain("field-svg", room.Markup);
        room.WaitForAssertion(() =>
        {
            Assert.Single(Calls("mlbAnimator.init"));
            var show = Calls("mlbAnimator.show").Last();
            Assert.Contains("\"outcome\":\"Setup\"", (string)show.Arguments[0]!);
            Assert.Contains("TOP OF INNING 1", (string)show.Arguments[2]!);
        });
    }

    [Fact]
    public void TheMessageBox_NamesTheHitterWithHisAverage_AndCountsThePitchersPitches()
    {
        StoredView("detailed");
        var room = StartedGame();

        room.WaitForAssertion(() =>
        {
            string message = (string)Calls("mlbAnimator.show").Last().Arguments[2]!;
            Assert.Matches(@"AT BAT \(\.\d{3}\)", message);
            Assert.Matches(@"\w+: 0 PITCHES$", message);
        });
    }

    [Fact]
    public void ParkData_TellsTheAnimatorWhetherThereIsARoof()
    {
        StoredView("detailed");
        StartedGame();

        Assert.Contains(Calls("mlbAnimator.show").Last().Arguments[1]!.GetType().GetProperties(), p => p.Name == "isDome");
    }

    [Fact]
    public void ParkData_CarriesTheWallColourAndTheDistancesTheFieldPaints()
    {
        StoredView("detailed");
        StartedGame();

        var park = Calls("mlbAnimator.show").Last().Arguments[1]!;
        var names = park.GetType().GetProperties().Select(p => p.Name).ToArray();

        foreach (var needed in new[] { "wallColor", "name", "leftField", "leftCenter", "center", "rightCenter", "rightField" })
            Assert.Contains(needed, names);
        // the default home park (Ebbets Field) has no wall colour of its own: the field keeps its default wall
        Assert.Null(park.GetType().GetProperty("wallColor")!.GetValue(park));
    }

    [Fact]
    public void TheSwitchChangesTheViewMidGame_AndRemembersIt()
    {
        StoredView("classic");
        var room = StartedGame();
        Assert.Contains("field-svg", room.Markup);

        room.Find("[data-testid=view-detailed]").Click();

        room.WaitForAssertion(() =>
        {
            Assert.Contains("detailed-field", room.Markup);
            Assert.DoesNotContain("field-svg", room.Markup);
        });
        Assert.Contains(Calls("mlbStorage.set"), c => (string)c.Arguments[0]! == "mlb-fieldview" && (string)c.Arguments[1]! == "detailed");

        room.Find("[data-testid=view-classic]").Click();

        room.WaitForAssertion(() => Assert.Contains("field-svg", room.Markup));
        Assert.Contains(Calls("mlbStorage.set"), c => (string)c.Arguments[1]! == "classic");
    }

    [Fact]
    public void TheSpeedButtons_AppearOnlyInDetailed_AndAreRemembered()
    {
        StoredView("classic");
        var room = StartedGame();
        Assert.Empty(room.FindAll("[data-testid=speed-fast]"));

        room.Find("[data-testid=view-detailed]").Click();
        room.WaitForAssertion(() => Assert.NotEmpty(room.FindAll("[data-testid=speed-fast]")));

        room.Find("[data-testid=speed-slow]").Click();
        Assert.Contains(Calls("mlbStorage.set"), c => (string)c.Arguments[0]! == "mlb-animspeed" && (string)c.Arguments[1]! == "slow");
    }

    [Fact]
    public void TheSetupScreen_OffersTheFieldView()
    {
        Render<MudPopoverProvider>();
        var room = Render<GameRoom>();
        room.WaitForState(() => room.Markup.Contains("PLAY BALL"));

        Assert.Contains("Field view", room.Markup);
    }

    // ── Detailed: each play is shown, and the page waits ─────────────────────

    [Fact]
    public void InDetailed_APlayIsSentToTheAnimator_AndTheGameStillAdvances()
    {
        StoredView("detailed");
        var room = StartedGame();
        string before = AtBat(room);

        Click(room, "PITCH");

        room.WaitForAssertion(() =>
        {
            var play = Assert.Single(PlayCalls());
            string json = (string)play.Arguments[0]!;
            Assert.Contains("\"actors\"", json);
            Assert.Contains("\"tracks\"", json);
            Assert.Equal(1.0, Convert.ToDouble(play.Arguments[2]));   // Normal speed
            Assert.NotEqual(before, AtBat(room));
        });
    }

    [Fact]
    public void InDetailed_ThePageKeepsTheOldSituationUntilThePlayIsOver()
    {
        StoredView("detailed");
        var room = StartedGame();
        var pending = JSInterop.SetupVoid("mlbAnimator.play", _ => true);   // the play does not finish until we say so
        string before = AtBat(room);
        int savesBefore = _store.Saves;

        Click(room, "PITCH");
        room.WaitForAssertion(() => Assert.Single(PlayCalls()));

        // The play has been applied and saved, but the page still shows the situation from before it.
        Assert.True(_store.Saves > savesBefore);
        Assert.Equal(before, AtBat(room));
        Assert.Empty(room.FindAll(".g-lastplay .mud-paper"));
        Assert.Empty(room.FindAll(".g-log .pbp-entry"));
        Assert.NotEmpty(room.FindAll("[data-testid=skip-play]"));

        pending.SetVoidResult();

        room.WaitForAssertion(() =>
        {
            Assert.NotEqual(before, AtBat(room));
            Assert.Empty(room.FindAll(".g-lastplay .mud-paper"));
            Assert.Single(room.FindAll(".g-log .pbp-entry"));
            Assert.Empty(room.FindAll("[data-testid=skip-play]"));
        });
    }

    // ── Replay ───────────────────────────────────────────────────────────────

    // The button is on the field, in the corner where Skip shows while a play is being shown, so it is there only between plays.
    private static bool CanReplay(IRenderedComponent<GameRoom> room) => room.FindAll("[data-testid=replay]").Count > 0;

    [Fact]
    public void TheReplayButton_AppearsOnlyOnceAPlayHasBeenShown_AndIsNotThereInClassic()
    {
        StoredView("detailed");
        var room = StartedGame();

        Assert.False(CanReplay(room));
        Click(room, "PITCH");
        room.WaitForAssertion(() => Assert.True(CanReplay(room)));

        room.Find("[data-testid=view-classic]").Click();
        room.WaitForAssertion(() => Assert.Empty(room.FindAll("[data-testid=replay]")));
    }

    [Fact]
    public void Replay_ShowsTheLastPlayAgain_UnderAReplayBanner_AndChangesNothingInTheGame()
    {
        StoredView("detailed");
        var room = StartedGame();
        Click(room, "PITCH");
        room.WaitForAssertion(() =>
        {
            Assert.Single(PlayCalls());
            Assert.True(CanReplay(room));
        });
        string atBat = AtBat(room);
        int saves = _store.Saves;

        room.Find("[data-testid=replay]").Click();

        room.WaitForAssertion(() => Assert.Equal(2, PlayCalls().Count));
        var first = MiniLeague.Engine.Animation.PlayScriptJson.Deserialize((string)PlayCalls()[0].Arguments[0]!)!;
        var again = MiniLeague.Engine.Animation.PlayScriptJson.Deserialize((string)PlayCalls()[1].Arguments[0]!)!;

        // The same play, move for move, with one more line for the message box.
        Assert.Equal(first.Actors, again.Actors);
        Assert.Equal(first.Tracks.SelectMany(t => t.Keys), again.Tracks.SelectMany(t => t.Keys));
        Assert.Equal(first.Duration, again.Duration);
        Assert.Contains(again.Cues, c => c.Kind == MiniLeague.Engine.Animation.CueKind.Banner && c.Value == "REPLAY" && c.T == 0);
        Assert.Equal(first.Cues.Count + 1, again.Cues.Count);
        Assert.Equal(Convert.ToDouble(PlayCalls()[0].Arguments[2]), Convert.ToDouble(PlayCalls()[1].Arguments[2]));   // at the chosen speed

        // The game did not move: same batter, nothing saved.
        room.WaitForAssertion(() => Assert.True(CanReplay(room)));
        Assert.Equal(atBat, AtBat(room));
        Assert.Equal(saves, _store.Saves);
    }

    [Fact]
    public void WhileAReplayRuns_SkipTakesItsPlace_NothingElseCanBeDone_AndWhenItEndsTheFieldIsShownAsItStands()
    {
        StoredView("detailed");
        var room = StartedGame();
        Click(room, "PITCH");
        room.WaitForAssertion(() => Assert.True(CanReplay(room)));
        string atBat = AtBat(room);
        int shows = Calls("mlbAnimator.show").Count;
        var pending = JSInterop.SetupVoid("mlbAnimator.play", _ => true);   // the replay does not finish until we say so

        room.Find("[data-testid=replay]").Click();
        room.WaitForAssertion(() => Assert.NotEmpty(room.FindAll("[data-testid=skip-play]")));

        Assert.False(CanReplay(room));
        int playsNow = Calls("mlbAnimator.play").Count;
        Click(room, "PITCH");   // a decision is ignored while a play is being shown
        Thread.Sleep(100);
        Assert.Equal(playsNow, Calls("mlbAnimator.play").Count);
        Assert.Equal(atBat, AtBat(room));

        pending.SetVoidResult();

        room.WaitForAssertion(() =>
        {
            Assert.Empty(room.FindAll("[data-testid=skip-play]"));
            Assert.True(CanReplay(room));
            Assert.True(Calls("mlbAnimator.show").Count > shows, "the field is drawn again as it stands");
        });
        Assert.Equal(atBat, AtBat(room));
    }

    [Fact]
    public void InDetailed_TheSkipButtonTellsTheAnimatorToFinishQuietly()
    {
        StoredView("detailed");
        var room = StartedGame();
        var pending = JSInterop.SetupVoid("mlbAnimator.play", _ => true);
        JSInterop.SetupVoid("mlbAnimator.skip");

        Click(room, "PITCH");
        room.WaitForAssertion(() => Assert.NotEmpty(room.FindAll("[data-testid=skip-play]")));

        room.Find("[data-testid=skip-play]").Click();

        Assert.Single(Calls("mlbAnimator.skip"));
        pending.SetVoidResult();
    }

    [Fact]
    public void InDetailed_ASecondDecisionDuringAPlayIsIgnored()
    {
        StoredView("detailed");
        var room = StartedGame();
        var pending = JSInterop.SetupVoid("mlbAnimator.play", _ => true);

        Click(room, "PITCH");
        room.WaitForAssertion(() => Assert.Single(PlayCalls()));
        int saves = _store.Saves;

        Click(room, "PITCH");   // the stale decision panel is still on screen

        Assert.Single(PlayCalls());
        Assert.Equal(saves, _store.Saves);   // and no second play was made
        pending.SetVoidResult();
        room.WaitForAssertion(() => Assert.Empty(room.FindAll("[data-testid=skip-play]")));
    }

    [Fact]
    public void InDetailed_ThePlaysOwnSoundsReplaceTheUsualOne_ForPlaysWithATemplate()
    {
        // A play whose script has sound cues plays them itself, so the page does not add the usual sound on top.
        StoredView("detailed");
        var room = StartedGame();

        Click(room, "PITCH");
        room.WaitForAssertion(() => Assert.Single(PlayCalls()));

        string json = (string)PlayCalls()[0].Arguments[0]!;
        bool scripted = json.Contains("\"kind\":\"sound\"");
        string[] pageSounds = Calls("mlbSound.play").Select(c => (string)c.Arguments[0]!).ToArray();
        if (scripted)
            Assert.DoesNotContain(pageSounds, s => s is "strikeout" or "walk" or "hit" or "homerun" or "out");
        else
            Assert.NotEmpty(pageSounds);
    }

    [Fact]
    public void InDetailed_TheComputerPlaysOnWithShortGaps_WhenItManagesBothTeams()
    {
        StoredView("detailed");
        var room = StartedGame(manage: "Neither");

        // Classic would wait 3 seconds between plays; the animated view only leaves a short gap after each play.
        room.WaitForAssertion(() => Assert.True(PlayCalls().Count >= 3), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void InDetailed_AScreenReaderIsToldWhoIsUp_AndThenHowEachPlayWent()
    {
        StoredView("detailed");
        var room = StartedGame();

        room.WaitForAssertion(() =>
        {
            string text = room.Find("[data-testid=play-announcement]").TextContent;
            Assert.Contains("TOP OF INNING 1", text);
            Assert.Contains("AT BAT", text);
        });

        Click(room, "PITCH");

        // After the play the result is announced, and it stays until the next play.
        room.WaitForAssertion(() =>
        {
            string text = room.Find("[data-testid=play-announcement]").TextContent;
            Assert.DoesNotContain("TOP OF INNING 1", text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        });
    }

    // ── The scenes around the plays ──────────────────────────────────────────

    private static string OutcomeOf(JSRuntimeInvocation call) =>
        System.Text.RegularExpressions.Regex.Match((string)call.Arguments[0]!, @"""outcome"":""(\w+)""").Groups[1].Value;

    [Fact]
    public void InDetailed_TheGameStartsWithAnIntroduction_ThatBringsItsOwnSound()
    {
        StoredView("detailed");
        var room = StartedGame();

        room.WaitForAssertion(() => Assert.NotEmpty(Calls("mlbAnimator.play")));
        Assert.Equal("PlayBall", OutcomeOf(Calls("mlbAnimator.play")[0]));
        Assert.Contains("\"value\":\"playball\"", (string)Calls("mlbAnimator.play")[0].Arguments[0]!);
        // The page does not add a second "play ball" on top of the one in the script.
        Assert.DoesNotContain(Calls("mlbSound.play"), c => (string)c.Arguments[0]! == "playball");
        Assert.NotEmpty(room.FindAll("[data-testid=detailed-field]"));
    }

    [Fact]
    public void InDetailed_ThePlayersCannotActUntilTheIntroductionIsOver()
    {
        StoredView("detailed");
        var pending = JSInterop.SetupVoid("mlbAnimator.play", _ => true);   // the introduction runs until we say so
        Render<MudPopoverProvider>();
        var room = Render<GameRoom>();
        room.WaitForState(() => room.Markup.Contains("PLAY BALL"));
        Click(room, "PLAY BALL");
        room.WaitForAssertion(() => Assert.NotEmpty(Calls("mlbAnimator.play")));

        Click(room, "PITCH");
        Assert.Single(Calls("mlbAnimator.play"));   // nothing happens: the game has not started yet

        pending.SetVoidResult();
        room.WaitForAssertion(() => Assert.Empty(room.FindAll("[data-testid=skip-play]")));
    }

    [Fact]
    public void InClassic_ThereIsNoIntroduction_AndThePageSaysPlayBall()
    {
        StoredView("classic");
        StartedGame();

        Assert.Empty(Calls("mlbAnimator.play"));
        Assert.Contains(Calls("mlbSound.play"), c => (string)c.Arguments[0]! == "playball");
    }

    [Fact]
    public void InDetailed_ANewHalfInningIsShown_WhenTheSideIsRetired()
    {
        StoredView("detailed");
        var room = StartedGame(manage: "Neither");

        room.WaitForAssertion(() => Assert.Contains(Calls("mlbAnimator.play"), c => OutcomeOf(c) == "HalfInning"),
            TimeSpan.FromSeconds(30));

        // It comes after the plays that ended the half-inning, and the introduction came first.
        var outcomes = Calls("mlbAnimator.play").Select(OutcomeOf).ToList();
        Assert.Equal("PlayBall", outcomes[0]);
        Assert.True(outcomes.IndexOf("HalfInning") > 3, string.Join(",", outcomes));
    }

    [Fact]
    public async Task LeavingTheGameRoom_StopsTheAnimator()
    {
        StoredView("detailed");
        var room = StartedGame();
        room.WaitForAssertion(() => Assert.Single(Calls("mlbAnimator.init")));

        await DisposeComponentsAsync();

        Assert.NotEmpty(Calls("mlbAnimator.stop"));
    }
}

/// <summary>The remembered Classic / Detailed choice and animation speed.</summary>
public class FieldViewPreferencesTests : BunitContext, IAsyncLifetime
{
    public FieldViewPreferencesTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private FieldViewPreferences Prefs() => new(JSInterop.JSRuntime);

    [Fact]
    public async Task Defaults_AreDetailedAtNormalSpeed()
    {
        var prefs = Prefs();
        await prefs.InitAsync();

        Assert.Equal(FieldViewMode.Detailed, prefs.View);
        Assert.Equal(AnimationSpeed.Normal, prefs.Speed);
        Assert.Equal(1.0, prefs.SpeedFactor);
    }

    [Fact]
    public async Task StoredChoices_AreRead()
    {
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-fieldview").SetResult("detailed");
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-animspeed").SetResult("fast");
        var prefs = Prefs();
        await prefs.InitAsync();

        Assert.Equal(FieldViewMode.Detailed, prefs.View);
        Assert.Equal(AnimationSpeed.Fast, prefs.Speed);
        Assert.Equal(2.0, prefs.SpeedFactor);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("42")]
    public async Task UnrecognisedStoredValues_FallBackToTheDefaults(string stored)
    {
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-fieldview").SetResult(stored);
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-animspeed").SetResult(stored);
        var prefs = Prefs();
        await prefs.InitAsync();

        Assert.Equal(FieldViewMode.Detailed, prefs.View);
        Assert.Equal(AnimationSpeed.Normal, prefs.Speed);
    }

    [Fact]
    public async Task Choices_AreWrittenToStorage()
    {
        var prefs = Prefs();
        await prefs.SetViewAsync(FieldViewMode.Detailed);
        await prefs.SetSpeedAsync(AnimationSpeed.Slow);

        Assert.Equal(FieldViewMode.Detailed, prefs.View);
        Assert.Equal(AnimationSpeed.Slow, prefs.Speed);
        var writes = JSInterop.Invocations["mlbStorage.set"].Select(i => ((string)i.Arguments[0]!, (string)i.Arguments[1]!)).ToList();
        Assert.Contains(("mlb-fieldview", "detailed"), writes);
        Assert.Contains(("mlb-animspeed", "slow"), writes);
    }

    [Fact]
    public async Task BlockedStorage_JustMeansTheDefaults_AndChoicesStillWorkForThisVisit()
    {
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-fieldview").SetException(new JSException("blocked"));
        JSInterop.Setup<bool>("mlbStorage.set", _ => true).SetException(new JSException("blocked"));
        var prefs = Prefs();

        await prefs.InitAsync();
        Assert.Equal(FieldViewMode.Detailed, prefs.View);

        await prefs.SetViewAsync(FieldViewMode.Classic);
        Assert.Equal(FieldViewMode.Classic, prefs.View);   // the choice works for this visit even if it cannot be saved
    }

    [Fact]
    public async Task ADeviceThatPrefersLessMotion_StartsAtFast_WhenNoSpeedWasChosen()
    {
        JSInterop.Setup<bool>("mlbAnimator.prefersReducedMotion").SetResult(true);
        var prefs = Prefs();
        await prefs.InitAsync();

        Assert.Equal(AnimationSpeed.Fast, prefs.Speed);
        Assert.Equal(FieldViewMode.Detailed, prefs.View);   // reduced motion does not switch the view
    }

    [Fact]
    public async Task ASpeedTheVisitorChose_BeatsTheDevicePreference()
    {
        JSInterop.Setup<bool>("mlbAnimator.prefersReducedMotion").SetResult(true);
        JSInterop.Setup<string?>("mlbStorage.get", "mlb-animspeed").SetResult("slow");
        var prefs = Prefs();
        await prefs.InitAsync();

        Assert.Equal(AnimationSpeed.Slow, prefs.Speed);
    }

    [Fact]
    public async Task IfTheDevicePreferenceCannotBeRead_TheSpeedIsNormal()
    {
        JSInterop.Setup<bool>("mlbAnimator.prefersReducedMotion").SetException(new JSException("not loaded"));
        var prefs = Prefs();
        await prefs.InitAsync();

        Assert.Equal(AnimationSpeed.Normal, prefs.Speed);
    }

    [Fact]
    public void FasterSpeedsPlayQuicker()
    {
        Assert.True(FieldViewPreferences.Factor(AnimationSpeed.Fast) > FieldViewPreferences.Factor(AnimationSpeed.Normal));
        Assert.True(FieldViewPreferences.Factor(AnimationSpeed.Normal) > FieldViewPreferences.Factor(AnimationSpeed.Slow));
    }
}
