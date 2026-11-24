using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Models;
using MiniLeague.Engine.Services;
using MiniLeague.Engine.Simulation;
using MiniLeague.Web.Components.Cast;
using MiniLeague.Web.Pages;
using MiniLeague.Web.Services;
using MudBlazor.Services;

namespace MiniLeague.Web.Tests;

/// <summary>Showing the game on a TV: what is sent, the app-bar button, and the /tv page that receives it.</summary>
public class CastTests : BunitContext, IAsyncLifetime
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

    public CastTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new HttpClient(new FileHandler()) { BaseAddress = new Uri("http://localhost/") });
        Services.AddScoped<TeamRepository>();
        Services.AddScoped<CastService>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private static GameState PlayedGame(int plays = 6)
    {
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));
        var game = new GameService(new AtBatResolver(new Random(3)), new ComputerManager(new Random(3)), new Random(3));
        game.StartGame(Load("1927-nyy.json"), Load("1955-bro.json"), ManagerControl.Computer, ManagerControl.Computer);
        for (int i = 0; i < plays; i++) game.ExecuteComputerTurn();
        return game.State!;
    }

    // ── What is sent ─────────────────────────────────────────────────────────

    [Fact]
    public void TheTvGetsTheGameWithoutItsLongHistory()
    {
        var state = PlayedGame(30);
        Assert.True(state.PlayByPlay.Count > 1 && state.Recap.Count > 1);

        var snapshot = GameSnapshots.FromJson(CastService.Encode(state))!;

        Assert.Single(snapshot.PlayByPlay);
        Assert.Equal(state.PlayByPlay[0].Description, snapshot.PlayByPlay[0].Description);
        Assert.Empty(snapshot.Recap);
        Assert.Equal(state.CurrentInning, snapshot.CurrentInning);
        Assert.Equal(state.Outs, snapshot.Outs);
    }

    [Fact]
    public async Task PublishingPassesTheGameToThePage()
    {
        await Services.GetRequiredService<CastService>().PublishAsync(PlayedGame(), detailed: false);

        var call = Assert.Single(JSInterop.Invocations["mlbCast.send"]);
        Assert.NotNull(GameSnapshots.FromJson((string)call.Arguments[0]!));
    }

    // ── The button ───────────────────────────────────────────────────────────

    [Fact]
    public void TheButtonIsHiddenWhenTheBrowserCannotCast()
    {
        JSInterop.Setup<bool>("mlbCast.init", _ => true).SetResult(false);

        var cut = Render<CastButton>();

        Assert.Empty(cut.FindAll("[data-testid=cast-button]"));
    }

    [Fact]
    public void TheButtonShowsWhenCastingAndWhenItStops()
    {
        JSInterop.Setup<bool>("mlbCast.init", _ => true).SetResult(true);
        var cut = Render<CastButton>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=cast-button]")));
        Assert.Contains("Show the game on a TV", cut.Find("[data-testid=cast-button]").GetAttribute("title"));

        cut.InvokeAsync(() => Services.GetRequiredService<CastService>().OnCastChanged(available: true, connected: true));
        Assert.Contains("Stop casting", cut.Find("[data-testid=cast-button]").GetAttribute("title"));

        cut.InvokeAsync(() => Services.GetRequiredService<CastService>().OnCastChanged(available: true, connected: false));
        Assert.Contains("Show the game on a TV", cut.Find("[data-testid=cast-button]").GetAttribute("title"));
    }

    // ── The TV page ──────────────────────────────────────────────────────────

    [Fact]
    public void TheTvWaitsForAGame_AndAsksTheBrowserToDeliverOne()
    {
        var cut = Render<Tv>();

        cut.WaitForAssertion(() => Assert.Contains("Waiting for a game", cut.Find("[data-testid=tv-waiting]").TextContent));
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbCast.listen"]));
    }

    [Fact]
    public async Task TheTvShowsTheGameItIsSent()
    {
        var state = PlayedGame(12);
        var cut = Render<Tv>();
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbCast.listen"]));

        await cut.InvokeAsync(() => cut.Instance.OnState(CastService.Encode(state), false));

        Assert.Equal(state.VisitorTotalScore.ToString(), cut.Find("[data-testid=tv-visitor-runs]").TextContent);
        Assert.Equal(state.HomeTotalScore.ToString(), cut.Find("[data-testid=tv-home-runs]").TextContent);
        Assert.Contains(state.CurrentInning.ToString(), cut.Find("[data-testid=tv-inning]").TextContent);
        Assert.Equal(state.CurrentBatter.Name, cut.Find("[data-testid=tv-batter]").TextContent);
        Assert.Equal(state.CurrentPitcher.Player.Name, cut.Find("[data-testid=tv-pitcher]").TextContent);
        Assert.Contains(state.PlayByPlay[0].Description, cut.Find("[data-testid=tv-last-play]").TextContent);
        Assert.NotEmpty(cut.FindAll("svg.field-svg"));
    }

    [Fact]
    public async Task TheTvShowsTheAnimatedField_WhenTheGameIsShownThatWay()
    {
        var state = PlayedGame(8);
        var cut = Render<Tv>();
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbCast.listen"]));

        await cut.InvokeAsync(() => cut.Instance.OnState(CastService.Encode(state), detailed: true));

        Assert.Single(cut.FindAll("[data-testid=detailed-field]"));
        Assert.Empty(cut.FindAll("svg.field-svg"));
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbAnimator.init"]));
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbCast.animatorUp"]));
        Assert.Equal(state.CurrentBatter.Name, cut.Find("[data-testid=tv-batter]").TextContent);

        // and back to the Classic field when the player switches
        await cut.InvokeAsync(() => cut.Instance.OnState(CastService.Encode(state), detailed: false));
        Assert.Empty(cut.FindAll("[data-testid=detailed-field]"));
        Assert.NotEmpty(cut.FindAll("svg.field-svg"));
    }

    [Fact]
    public async Task TheTvDrawsTheRunnersOnBase()
    {
        var state = PlayedGame(4);
        state.RunnerOnFirst = new BaseRunner { Player = state.CurrentBatter, BattingSlot = 0 };
        state.RunnerOnThird = new BaseRunner { Player = state.CurrentBatter, BattingSlot = 0 };
        var cut = Render<Tv>();
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbCast.listen"]));

        await cut.InvokeAsync(() => cut.Instance.OnState(CastService.Encode(state), false));
        Assert.Equal(2, cut.FindAll("circle[r='6'][fill='#ffb300']").Count);

        state.RunnerOnFirst = null;
        state.RunnerOnThird = null;
        await cut.InvokeAsync(() => cut.Instance.OnState(CastService.Encode(state), false));
        Assert.Empty(cut.FindAll("circle[r='6'][fill='#ffb300']"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTvShowsAFinishedGame(bool detailed)
    {
        var state = PlayedGame(0);
        Team Load(string file) => TeamLoader.Parse(File.ReadAllText(Path.Combine(DataDir, "teams", file)));
        var game = new GameService(new AtBatResolver(new Random(5)), new ComputerManager(new Random(5)), new Random(5));
        game.StartGame(Load("1927-nyy.json"), Load("1955-bro.json"), ManagerControl.Computer, ManagerControl.Computer);
        for (int i = 0; i < 1500 && game.State!.Status != GameStatus.Final; i++) game.ExecuteComputerTurn();
        Assert.Equal(GameStatus.Final, game.State!.Status);

        var cut = Render<Tv>();
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbCast.listen"]));
        await cut.InvokeAsync(() => cut.Instance.OnState(CastService.Encode(game.State), detailed));

        Assert.Contains("FINAL", cut.Find("[data-testid=tv-inning]").TextContent);
        Assert.Equal(game.State.VisitorTotalScore.ToString(), cut.Find("[data-testid=tv-visitor-runs]").TextContent);
    }

    [Fact]
    public async Task TheTvIgnoresMessagesItCannotRead()
    {
        var cut = Render<Tv>();
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["mlbCast.listen"]));

        await cut.InvokeAsync(() => cut.Instance.OnState("not a game", false));

        Assert.Single(cut.FindAll("[data-testid=tv-waiting]"));
    }
}
