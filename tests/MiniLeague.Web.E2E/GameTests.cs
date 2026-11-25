using Microsoft.Playwright;

namespace MiniLeague.Web.E2E;

/// <summary>The game in a real browser: the things the component tests cannot show (start-up order, timing, the real animator).</summary>
[Collection("app")]
public class GameTests(AppFixture app)
{
    [Fact]
    public async Task DefensiveAlignmentMovesFieldersBeforeThePitch()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: true);
        await player.StartGameAsync(manage: "Home team");
        await player.WaitUntilIdleAsync();
        await player.Page.Locator("[data-testid=speed-slow]").ClickAsync();
        await player.Page.Locator(".decision-panel .mud-button-group-root").Last.Locator("button").Nth(1).ClickAsync();
        await player.Page.Locator("[data-testid=skip-play]").WaitForAsync();
        Assert.Equal(0, await player.Page.Locator(".g-log .pbp-entry").CountAsync());
        Assert.True(await player.Page.Locator(".decision-panel .decision-commit").IsDisabledAsync());
        await player.WaitUntilIdleAsync();
        await player.PitchAsync();
        Assert.Equal(1, await player.Page.Locator(".g-log .pbp-entry").CountAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task ClassicFieldersMoveWhenTheDefenseChangesPosition()
    {
        await using var player = await GamePage.OpenAsync(app, "/game");
        await player.StartGameAsync(manage: "Home team");
        var firstBaseman = player.Page.Locator(".fielder-group").Nth(2);
        string? before = await firstBaseman.GetAttributeAsync("style");

        await player.Page.Locator(".decision-panel .mud-button-group-root").Last.Locator("button").Nth(1).ClickAsync();
        await player.Page.WaitForFunctionAsync("old => document.querySelectorAll('.fielder-group')[2]?.getAttribute('style') !== old", before);
        Assert.Equal(0, await player.Page.Locator(".g-log .pbp-entry").CountAsync());
        Assert.NotEqual(before, await firstBaseman.GetAttributeAsync("style"));
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task ComputerMovesTheInfieldInBeforeItsPitch()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: true,
            script: "localStorage.setItem('mlb-animspeed', 'slow');", savedGame: SavedGames.ComputerInfieldIn());
        await player.Page.Locator("[data-testid=resume-card]").WaitForAsync();
        await player.Page.Locator("button", new() { HasText = "RESUME" }).ClickAsync();

        await player.Page.Locator("[data-testid=skip-play]").WaitForAsync();
        Assert.Equal(0, await player.Page.Locator(".g-log .pbp-entry").CountAsync());
        await player.Page.Locator("[data-testid=skip-play]").ClickAsync();
        await player.Page.Locator(".g-log .pbp-entry").First.WaitForAsync();
        Assert.Empty(player.Errors);
    }

    [Theory]
    [InlineData("Shift Pull")]
    [InlineData("Shift Oppo")]
    public async Task ShiftButtonsMoveFieldersBeforeThePitch(string label)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: true);
        await player.StartGameAsync(manage: "Home team");
        await player.WaitUntilIdleAsync();
        await player.Page.Locator("[data-testid=speed-slow]").ClickAsync();
        await player.Page.Locator(".decision-panel .mud-button-group-root").Last
            .Locator("button", new() { HasTextString = label }).ClickAsync();
        // The scene plays (fielders walk to their new spots) and the pitch button is locked until it ends.
        await player.Page.Locator("[data-testid=skip-play]").WaitForAsync();
        Assert.True(await player.Page.Locator(".decision-panel .decision-commit").IsDisabledAsync());
        await player.WaitUntilIdleAsync();
        await player.PitchAsync();
        Assert.Equal(1, await player.Page.Locator(".g-log .pbp-entry").CountAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task PlayLogCanBeSearchedAndReturnedToLatestOnPhone()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", width: 390, height: 844);
        await player.StartGameAsync();
        await player.PitchAsync();
        await player.PitchAsync();

        var entries = player.Page.Locator(".g-log .pbp-entry");
        Assert.Equal(2, await entries.CountAsync());
        await player.Page.Locator(".g-log").ScrollIntoViewIfNeededAsync();
        await player.Page.Locator("#pbp-search").FillAsync("no matching play");
        Assert.Equal(0, await entries.CountAsync());
        Assert.Contains("No plays match", await player.Page.Locator(".pbp-empty").InnerTextAsync());
        await player.Page.Locator(".pbp-latest").ClickAsync();
        Assert.Equal(2, await entries.CountAsync());
        Assert.Equal("", await player.Page.Locator("#pbp-search").InputValueAsync());
        Assert.True(await player.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task HelpExplainsGameControlsBeforeAndDuringPlay()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", width: 390, height: 844);
        await player.Page.Locator("[data-testid=matchup-preview]").WaitForAsync();
        var help = player.Page.Locator("[data-testid=game-help]");
        await help.ClickAsync();
        var guide = player.Page.Locator("[data-testid=game-help-guide]");
        await guide.WaitForAsync();
        string text = await guide.InnerTextAsync();
        Assert.Contains("Game options", text);
        Assert.Contains("Steal", text);
        Assert.Contains("4–10 batters", text);
        Assert.Contains("Shift + number", text);
        Assert.True(await player.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        await player.CloseDialogAsync();

        await player.StartGameAsync();
        await help.ClickAsync();
        await guide.WaitForAsync();
        await player.CloseDialogAsync();
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task SetupKeepsOptionalChoicesCollapsedUntilRequested()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", width: 390, height: 844);
        await player.Page.Locator("[data-testid=matchup-preview]").WaitForAsync();
        var options = player.Page.Locator("[data-testid=setup-options]");
        Assert.False(await options.EvaluateAsync<bool>("element => element.open"));
        var play = player.Page.Locator("button", new() { HasText = "PLAY BALL" });
        // The manager profile follows the optional choices on a phone, so the action can sit below the fold.
        // Check that it remains reachable without opening those choices.
        await play.ScrollIntoViewIfNeededAsync();
        var bounds = await play.BoundingBoxAsync();
        Assert.NotNull(bounds);
        Assert.True(bounds.Y >= 0 && bounds.Y + bounds.Height <= player.Page.ViewportSize!.Height);

        await options.Locator("summary").ClickAsync();
        Assert.True(await options.EvaluateAsync<bool>("element => element.open"));
        Assert.True(await options.Locator(".mud-select .mud-input-control").First.IsVisibleAsync());
        await options.Locator("summary").ClickAsync();
        Assert.False(await options.EvaluateAsync<bool>("element => element.open"));
        await player.StartGameAsync();
        await player.Page.Locator("[data-testid=pitch-context]").WaitForAsync();
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task PhoneScoreAndSupportingCardsStayCompact()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", width: 390, height: 844);
        await player.StartGameAsync();

        var strip = player.Page.Locator("[data-testid=mobile-score]");
        Assert.False(await strip.IsVisibleAsync());
        var pitching = player.Page.Locator("[data-testid=mobile-pitching-toggle]");
        Assert.True(await pitching.IsVisibleAsync());
        Assert.False(await player.Page.Locator("#pitching-details").IsVisibleAsync());
        await pitching.ClickAsync();
        Assert.True(await player.Page.Locator("#pitching-details").IsVisibleAsync());

        await player.Page.Locator(".g-lineups").ScrollIntoViewIfNeededAsync();
        await player.Page.WaitForFunctionAsync("() => document.querySelector('[data-testid=mobile-score]')?.classList.contains('visible')");
        Assert.True(await strip.IsVisibleAsync());
        Assert.Contains("NYY", await strip.InnerTextAsync());
        Assert.True(await player.Page.Locator("#lineup-visitor-panel").IsVisibleAsync());
        Assert.False(await player.Page.Locator("#lineup-home-panel").IsVisibleAsync());
        await player.Page.Locator("#lineup-home-tab").ClickAsync();
        Assert.False(await player.Page.Locator("#lineup-visitor-panel").IsVisibleAsync());
        Assert.True(await player.Page.Locator("#lineup-home-panel").IsVisibleAsync());
        await player.Page.SetViewportSizeAsync(1280, 800);
        Assert.False(await strip.IsVisibleAsync());
        Assert.True(await player.Page.Locator("#pitching-details").IsVisibleAsync());
        Assert.True(await player.Page.Locator("#lineup-visitor-panel").IsVisibleAsync());
        Assert.True(await player.Page.Locator("#lineup-home-panel").IsVisibleAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task MatchupAndDecisionCardsStayReadableAcrossScreenSizes()
    {
        await using var player = await GamePage.OpenAsync(app, "/game");
        var preview = player.Page.Locator("[data-testid=matchup-preview]");
        await preview.WaitForAsync();
        Assert.Contains("NYY", await preview.InnerTextAsync());
        Assert.Contains("BRO", await preview.InnerTextAsync());
        Assert.Contains("Ebbets Field", await preview.InnerTextAsync());
        string originalMatchup = await preview.InnerTextAsync();
        await player.Page.Locator(".mud-select .mud-input-control").First.ClickAsync();
        await player.Page.Locator(".mud-list-item").Nth(2).ClickAsync();
        Assert.NotEqual(originalMatchup, await preview.InnerTextAsync());

        await player.Page.SetViewportSizeAsync(390, 844);
        await player.Page.SetViewportSizeAsync(1280, 800);

        await player.StartGameAsync();
        var context = player.Page.Locator("[data-testid=pitch-context]");
        await context.WaitForAsync();
        Assert.Contains("Bases empty", await context.InnerTextAsync());
        Assert.Contains(await player.BatterAsync(), await context.InnerTextAsync());
        Assert.Contains("BATTING APPROACH", await player.Page.Locator(".decision-panel").InnerTextAsync());
        await player.PitchAsync();
        Assert.Equal(1, await player.Page.Locator(".g-log .pbp-entry").CountAsync());
        Assert.Equal(0, await player.Page.Locator(".g-lastplay").CountAsync());
        await player.Page.SetViewportSizeAsync(390, 844);
        Assert.True(await player.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task TheHomePageLoads()
    {
        await using var player = await GamePage.OpenAsync(app, "/");

        await player.Page.Locator(".start-page").GetByRole(AriaRole.Link, new() { Name = "PLAY BALL" }).WaitForAsync();

        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task TheMenuOpensTheAboutPageWithTheDisclaimerAndTheCredits()
    {
        await using var player = await GamePage.OpenAsync(app, "/");
        await player.Page.Locator(".start-page").GetByRole(AriaRole.Link, new() { Name = "PLAY BALL" }).WaitForAsync();

        await player.Page.Locator("button.mud-icon-button").First.ClickAsync();   // the menu button
        await player.Page.Locator("[data-testid=about-link]").ClickAsync();

        await player.Page.Locator("[data-testid=about]").WaitForAsync();
        Assert.EndsWith("/about", player.Page.Url);
        string text = await player.Page.Locator("[data-testid=about]").InnerTextAsync();
        Assert.Contains("not affiliated with, authorized by or endorsed by", text);
        Assert.Contains("Major League Baseball", text);
        Assert.Contains("Lahman Baseball Database", text);
        Assert.Contains("CC BY-SA 3.0", text);
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGameCanBePlayed(bool detailed)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed);

        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        string firstBatter = await player.BatterAsync();
        await player.PitchAsync();
        await player.PitchAsync();

        Assert.False(string.IsNullOrWhiteSpace(await player.LastPlayAsync()));
        Assert.False(string.IsNullOrWhiteSpace(firstBatter));
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task DetailedPlayRevealsTheLogAfterAnimationOrSkip()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: true);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();

        var entries = player.Page.Locator(".g-log .pbp-entry");
        Assert.Equal(0, await entries.CountAsync());

        await player.Page.Locator(".decision-panel button:has-text('PITCH')").ClickAsync();
        await player.Page.WaitForFunctionAsync("() => window.mlbAnimator?.playing === true");
        Assert.Equal(0, await entries.CountAsync());
        Assert.Equal(0, await player.Page.Locator(".g-lastplay .mud-paper").CountAsync());

        await player.WaitUntilIdleAsync();
        Assert.Equal(1, await entries.CountAsync());

        await player.Page.Locator(".decision-panel button:has-text('PITCH')").ClickAsync();
        await player.Page.WaitForFunctionAsync("() => window.mlbAnimator?.playing === true");
        Assert.Equal(1, await entries.CountAsync());
        await player.Page.Locator("[data-testid=skip-play]").ClickAsync();
        await player.WaitUntilIdleAsync();
        Assert.Equal(2, await entries.CountAsync());
        Assert.Equal(0, await player.Page.Locator(".g-lastplay .mud-paper").CountAsync());
        Assert.Empty(player.Errors);
    }

    // The computer deciding for both sides must keep the game moving. (In the animated view the game once sat on
    // "Computer deciding..." for ever: the introduction was asked for before the page had noted that it was due.)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheComputerPlaysBothSidesAndTheGameKeepsMoving(bool detailed)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed);

        await player.StartGameAsync(manage: "Neither");

        var plays = new HashSet<string>();
        bool moved = await GamePage.UntilAsync(async () =>
        {
            string play = await player.LastPlayAsync();
            if (play.Length > 0) plays.Add(play);
            return plays.Count >= 3;
        }, seconds: 90);

        Assert.True(moved, $"The game stopped moving after {plays.Count} play(s).");
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }

    // WHY: the freeze above came from a race at start-up, and a race can pass a single try. Start the animated game
    // several times, each in a fresh browser window (a cold start), and the computer must play every time.
    [Fact]
    public async Task TheComputerStartsPlayingEveryTimeAnAnimatedGameIsStarted()
    {
        for (int round = 1; round <= 5; round++)
        {
            await using var player = await GamePage.OpenAsync(app, "/game", detailed: true);
            await player.StartGameAsync(manage: "Neither");

            bool played = await GamePage.UntilAsync(async () => (await player.LastPlayAsync()).Length > 0, seconds: 45);

            Assert.True(played, $"Start {round} of 5: the computer never played.");
            Assert.False(await player.ErrorBannerShownAsync());
            Assert.Empty(player.Errors);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASavedGameCanBeResumed(bool detailed)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        await player.PitchAsync();
        string batter = await player.BatterAsync();

        await player.Page.ReloadAsync();
        await player.Page.Locator("[data-testid=resume-card]").WaitForAsync();
        await player.Page.Locator("button", new() { HasText = "RESUME" }).ClickAsync();
        await player.Page.Locator("[data-testid=at-bat-name]").WaitForAsync();

        Assert.Equal(batter, await player.BatterAsync());
        await player.WaitUntilIdleAsync();
        await player.PitchAsync();
        Assert.False(string.IsNullOrWhiteSpace(await player.LastPlayAsync()));
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }
}
