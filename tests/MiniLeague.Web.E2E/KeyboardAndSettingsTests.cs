using Microsoft.Playwright;

namespace MiniLeague.Web.E2E;

/// <summary>The keyboard, which the component tests cannot press, and the choices the game remembers.</summary>
[Collection("app")]
public class KeyboardAndSettingsTests(AppFixture app)
{
    private static async Task<bool> IsChosenAsync(IPage page, string selector) =>
        (await page.Locator(selector).First.GetAttributeAsync("class") ?? "").Contains("mud-button-filled");

    private static ILocator Strategy(IPage page, string label) =>
        page.Locator(".decision-panel button", new PageLocatorOptions { HasText = label });

    [Theory]
    [InlineData(false, 390)]
    [InlineData(true, 1280)]
    public async Task HintOpensByKeyboardAndKeepsPlayKeysInsideTheDialog(bool detailed, int width)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: detailed, width: width);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        await player.Page.Locator("[data-testid=hint-button]").WaitForAsync();
        string before = await player.LastPlayAsync();

        await player.Page.Keyboard.PressAsync("KeyH");
        var card = player.Page.Locator("[data-testid=play-hint-card]");
        await card.WaitForAsync();
        Assert.Contains("MY CALL", await card.InnerTextAsync());
        Assert.DoesNotMatch(@"\d", await card.InnerTextAsync());
        await player.Page.Locator(".mud-dialog").ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = Path.Combine(Path.GetTempPath(), detailed ? "minileague-hint-detailed.png" : "minileague-hint-phone.png"),
        });
        await player.Page.Keyboard.PressAsync("Digit2");
        Assert.Equal(before, await player.LastPlayAsync());
        await player.Page.Keyboard.PressAsync("KeyH");
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.True(await player.Page.Locator("[data-testid=hint-button]").EvaluateAsync<bool>("el => document.activeElement === el"));

        await player.Page.Locator("[data-testid=hint-button]").ClickAsync();
        await card.WaitForAsync();
        await player.Page.Keyboard.PressAsync("Escape");
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task TwoHumanHintSwitchesSidesAndNamesTheFieldingCall()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", width: 390);
        await player.StartGameAsync(manage: "Both teams");
        await player.Page.Locator(".decision-panel").ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = Path.Combine(Path.GetTempPath(), "minileague-hint-budgets-phone.png"),
        });
        await player.Page.Locator("[data-testid=hint-button]").ClickAsync();
        var card = player.Page.Locator("[data-testid=play-hint-card]");
        await card.WaitForAsync();
        Assert.Contains("You are batting", await card.InnerTextAsync());
        await player.Page.GetByRole(AriaRole.Button, new() { Name = "Fielding hint" }).ClickAsync();
        Assert.Contains("You are fielding", await card.InnerTextAsync());
        Assert.Contains("Throw a", await card.InnerTextAsync());
        Assert.True(await player.Page.Locator(".play-hint-heading").EvaluateAsync<bool>("el => document.activeElement === el"));
        await player.Page.Locator("[data-testid=hint-close]").ClickAsync();
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task ReopeningAHintAfterResumeDoesNotSpendAnotherOne()
    {
        await using var player = await GamePage.OpenAsync(app, "/game");
        await player.StartGameAsync();
        var button = player.Page.Locator("[data-testid=hint-button]");
        await button.WaitForAsync();
        Assert.Contains("10 LEFT", await button.InnerTextAsync());

        await button.ClickAsync();
        var card = player.Page.Locator("[data-testid=play-hint-card]");
        await card.WaitForAsync();
        await player.Page.Locator("[data-testid=hint-close]").ClickAsync();
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.Contains("9 LEFT", await button.InnerTextAsync());

        await player.Page.ReloadAsync();
        await player.Page.Locator("button", new() { HasText = "RESUME" }).ClickAsync();
        await button.WaitForAsync();
        Assert.Contains("9 LEFT", await button.InnerTextAsync());
        await button.ClickAsync();
        await card.WaitForAsync();
        await player.Page.Locator("[data-testid=hint-close]").ClickAsync();
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.Contains("9 LEFT", await button.InnerTextAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task GameOptionsCanHidePlayHintsForTheGame()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", width: 390);
        await player.Page.Locator("[data-testid=setup-options] summary").ClickAsync();
        var option = player.Page.Locator("[data-testid=show-play-hints]");
        Assert.True(await option.IsCheckedAsync());
        await player.Page.GetByText("Show Play Hints").ClickAsync();
        Assert.False(await option.IsCheckedAsync());
        await player.Page.Locator("[data-testid=setup-options]").ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = Path.Combine(Path.GetTempPath(), "minileague-hints-option.png"),
        });

        await player.Page.Locator("button", new() { HasText = "PLAY BALL" }).ClickAsync();
        await player.Page.Locator(".decision-panel").WaitForAsync();
        Assert.Equal(0, await player.Page.Locator("[data-testid=hint-button]").CountAsync());
        await player.Page.Keyboard.PressAsync("KeyH");
        Assert.Equal(0, await player.Page.Locator("[data-testid=play-hint-card]").CountAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task StealIsUnavailableWithEmptyBases()
    {
        await using var player = await GamePage.OpenAsync(app, "/game");
        await player.StartGameAsync();
        await player.Page.Locator(".decision-panel").WaitForAsync();

        Assert.True(await Strategy(player.Page, "Steal").IsDisabledAsync());
        await player.Page.Keyboard.PressAsync("Digit4");
        Assert.True(await IsChosenAsync(player.Page, ".decision-panel button:has-text('SWING AWAY')"));
        Assert.False(await IsChosenAsync(player.Page, ".decision-panel button:has-text('STEAL')"));
        await player.Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Path.GetTempPath(), "minileague-steal-disabled.png") });
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task TheKeyboardPlaysTheGame()
    {
        await using var player = await GamePage.OpenAsync(app, "/game");
        await player.StartGameAsync();
        await player.Page.Locator(".decision-panel").WaitForAsync();
        Assert.True(await IsChosenAsync(player.Page, ".decision-panel button:has-text('SWING AWAY')"));

        // 3 picks "safe", 2 "aggressive": one at a time
        await player.Page.Keyboard.PressAsync("Digit3");
        Assert.True(await GamePage.UntilAsync(() => IsChosenAsync(player.Page, ".decision-panel button:has-text('SAFE')"), seconds: 5));
        Assert.False(await IsChosenAsync(player.Page, ".decision-panel button:has-text('SWING AWAY')"));
        await player.Page.Keyboard.PressAsync("Digit2");
        Assert.True(await GamePage.UntilAsync(() => IsChosenAsync(player.Page, ".decision-panel button:has-text('AGGRESSIVE')"), seconds: 5));
        Assert.False(await IsChosenAsync(player.Page, ".decision-panel button:has-text('SAFE')"));

        // Enter pitches
        await player.Page.Keyboard.PressAsync("Enter");
        Assert.True(await GamePage.UntilAsync(async () => (await player.LastPlayAsync()).Length > 0, seconds: 15),
            "Enter did not play the pitch");

        // B opens the box score, S the substitutions; a dialog that is open keeps the keys to itself
        await player.Page.Keyboard.PressAsync("KeyB");
        await player.Page.Locator("[data-testid=box-score]").WaitForAsync();
        await player.CloseDialogAsync();

        await player.Page.Keyboard.PressAsync("KeyS");
        var dialog = player.Page.Locator(".mud-dialog", new PageLocatorOptions { HasText = "Substitutions" });
        await dialog.WaitForAsync();
        await player.CloseDialogAsync();

        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }

    // The field view, the animation speed and the sound setting stay as the player left them.
    [Fact]
    public async Task TheFieldViewTheSpeedAndTheMuteSettingAreRemembered()
    {
        // WHY: setPreferences is off and the Classic view is chosen on the first load only (a flag in storage): the
        // init script runs again on the reload, and choosing again there would hide whether the app remembered anything.
        await using var player = await GamePage.OpenAsync(app, "/game", setPreferences: false, script: """
            try {
              if (!localStorage.getItem('mlb-e2e-seeded')) {
                localStorage.setItem('mlb-fieldview', 'classic');
                localStorage.setItem('mlb-e2e-seeded', '1');
              }
            } catch { }
            """);
        await player.StartGameAsync();
        Assert.Empty(await player.Page.Locator("[data-testid=detailed-field]").AllAsync());

        // choose the animated field, the slow speed and no sound
        await player.Page.Locator("[data-testid=view-detailed]").ClickAsync();
        await player.Page.Locator("[data-testid=detailed-field]").WaitForAsync();
        await player.Page.Locator("[data-testid=speed-slow]").ClickAsync();
        await player.Page.Locator("button[title='Mute sound']").ClickAsync();
        await player.Page.Locator("button[title='Unmute sound']").WaitForAsync();

        await player.Page.ReloadAsync();
        await player.Page.Locator("button", new() { HasText = "RESUME" }).ClickAsync();
        await player.Page.Locator("[data-testid=detailed-field]").WaitForAsync();
        Assert.True(await IsChosenAsync(player.Page, "[data-testid=speed-slow]"), "the slow speed was not remembered");
        Assert.True(await player.Page.Locator("button[title='Unmute sound']").IsVisibleAsync(), "the mute setting was not remembered");

        // and back to the overhead view
        await player.Page.Locator("[data-testid=view-classic]").ClickAsync();
        await player.Page.Locator("svg.field-svg").WaitForAsync();
        await player.Page.ReloadAsync();
        await player.Page.Locator("button", new() { HasText = "RESUME" }).ClickAsync();
        await player.Page.Locator("svg.field-svg").WaitForAsync();
        Assert.True(await IsChosenAsync(player.Page, "[data-testid=view-classic]"), "the Classic view was not remembered");
        Assert.Empty(await player.Page.Locator("[data-testid=detailed-field]").AllAsync());
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }
}
