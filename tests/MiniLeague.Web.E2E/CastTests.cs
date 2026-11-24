using Microsoft.Playwright;

namespace MiniLeague.Web.E2E;

/// <summary>Showing the game on a TV. A real Chromecast cannot be used in a test, so the Cast connection is a pretend one.</summary>
[Collection("app")]
public class CastTests(AppFixture app)
{
    private static async Task<double> TopAsync(GamePage player, string selector) =>
        await player.Page.EvaluateAsync<double>("s => document.querySelector(s).getBoundingClientRect().top", selector);

    [Fact]
    public async Task TheTvPageWaitsForAGame()
    {
        await using var tv = await GamePage.OpenAsync(app, "/tv", script: GamePage.FakeCastReceiver);

        await tv.Page.Locator("[data-testid=tv-waiting]").WaitForAsync();

        Assert.Contains("Waiting for a game", await tv.Page.Locator("[data-testid=tv-waiting]").InnerTextAsync());
        Assert.Empty(tv.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhileCasting_TheFieldLeavesThisScreen_AndComesBack(bool detailed)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed, script: GamePage.FakeCastSender);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        string field = detailed ? "[data-testid=detailed-field]" : "svg.field-svg";
        Assert.True(await player.Page.Locator(field).IsVisibleAsync());

        await player.StartCastingAsync();

        Assert.False(await player.Page.Locator(field).IsVisibleAsync());   // parked out of sight
        Assert.True(await player.Page.Locator("[data-testid=casting-note]").IsVisibleAsync());
        double left = await player.Page.EvaluateAsync<double>(
            "() => document.querySelector('[data-testid=field-region]').getBoundingClientRect().left");
        Assert.True(left < -5000, $"the field region is at {left}");
        if (detailed)   // still drawing (the TV gets its plays), so it must keep a real size
            Assert.True(await player.Page.EvaluateAsync<bool>(
                "() => document.querySelector('.detailed-canvas').getBoundingClientRect().width > 100"));

        await player.PitchAsync();   // the game is still played from here
        Assert.False(string.IsNullOrWhiteSpace(await player.LastPlayAsync()));

        await player.StopCastingAsync();
        Assert.True(await GamePage.UntilAsync(async () =>
            !(await player.Page.GetAttributeAsync("[data-testid=field-region]", "class"))!.Contains("cast-away")));
        Assert.True(await player.Page.Locator(field).IsVisibleAsync());
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task TheTvIsSentTheGame_AndThePlaysInTheAnimatedView()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: true, script: GamePage.FakeCastSender);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        await player.StartCastingAsync();
        await player.PitchAsync();

        var kinds = (await player.SentToTvAsync()).Select(GamePage.KindOf).ToHashSet();

        Assert.Contains("state", kinds);
        Assert.Contains("play", kinds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTvPageShowsWhatThePlayersPageSent(bool detailed)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed, script: GamePage.FakeCastSender);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        await player.StartCastingAsync();
        await player.PitchAsync();
        await player.PitchAsync();
        string[] messages = await player.SentToTvAsync();

        await using var tv = await GamePage.OpenAsync(app, "/tv", detailed, script: GamePage.FakeCastReceiver);
        await tv.Page.WaitForFunctionAsync("() => window.__sent.length > 0");   // the TV page has said hello
        foreach (string message in messages)
        {
            await tv.Page.EvaluateAsync("m => window.__conn.onmessage({ data: m })", message);
            await Task.Delay(100);
        }

        await tv.Page.Locator("[data-testid=tv]").WaitForAsync();
        string batter = await player.BatterAsync();
        Assert.True(await GamePage.UntilAsync(async () =>
            await tv.Page.Locator("[data-testid=tv-batter]").InnerTextAsync() == batter));
        Assert.True(await tv.Page.Locator(detailed ? "[data-testid=detailed-field]" : "svg.field-svg").IsVisibleAsync());
        Assert.False(string.IsNullOrWhiteSpace(await tv.Page.Locator("[data-testid=tv-last-play]").InnerTextAsync()));
        Assert.False(await tv.ErrorBannerShownAsync());
        Assert.Empty(tv.Errors);
    }

    // The "TV view" menu item: a second tab that follows the game over the browser's own channel (no Cast needed).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATvViewOpenedByHandFollowsTheGame(bool detailed)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        var tv = await player.NewTabAsync(app, "/tv");
        await tv.Locator("[data-testid=tv]").WaitForAsync();   // brought up to date as it opens

        await player.PitchAsync();

        string batter = await player.BatterAsync();
        Assert.True(await GamePage.UntilAsync(async () =>
            await tv.Locator("[data-testid=tv-batter]").InnerTextAsync() == batter));
        Assert.Equal(detailed, await tv.Locator("[data-testid=detailed-field]").IsVisibleAsync());
        Assert.False(await tv.Locator("#blazor-error-ui").IsVisibleAsync());
        Assert.Empty(player.Errors);
    }

    // While casting, the free space must be used: no hole where the field was, and no sideways scrolling.
    [Theory]
    [InlineData(390, 800)]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    public async Task WhileCasting_TheLayoutUsesTheRoomTheFieldLeft(int width, int height)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", width: width, height: height, script: GamePage.FakeCastSender);
        await player.StartGameAsync();

        await player.StartCastingAsync();

        Assert.True(await player.Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
        if (width >= 900)
        {
            // the lineups and the decisions sit side by side, from the same height
            double gap = Math.Abs(await TopAsync(player, ".g-lineups") - await TopAsync(player, ".g-decision"));
            Assert.True(gap < 60, $"the lineups start {gap}px away from the decisions");
        }
        else
        {
            // on a phone the decisions follow the scoreboard
            Assert.True(await TopAsync(player, ".g-decision") < 600);
        }
        Assert.Empty(player.Errors);
    }
}
