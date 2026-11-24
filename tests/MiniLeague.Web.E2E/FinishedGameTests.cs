using Microsoft.Playwright;

namespace MiniLeague.Web.E2E;

/// <summary>
/// The end of a game, in a real browser. Playing a whole game takes minutes, so the game starts from a save made one
/// play before the last (see <see cref="SavedGames"/>): the computer plays the final play and the game is over.
/// </summary>
[Collection("app")]
public class FinishedGameTests(AppFixture app)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFinishedGameShowsItsResult_ItsBoxScoreAndRecap_AndIsNotKept(bool detailed)
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed, savedGame: SavedGames.OnePlayFromTheEnd());
        var tv = await player.NewTabAsync(app, "/tv");   // watching from the start: it must see the end too

        await player.Page.Locator("[data-testid=resume-card]").WaitForAsync();
        await player.Page.Locator("button", new() { HasText = "RESUME" }).ClickAsync();
        var gameRecap = player.Page.Locator("button", new() { HasText = "Game recap" });   // only a finished game has it
        await gameRecap.WaitForAsync(new LocatorWaitForOptions { Timeout = 120_000 });

        // the box score and the recap open and say something
        await player.Page.Locator("button", new() { HasText = "Box score" }).First.ClickAsync();
        var boxScore = player.Page.Locator("[data-testid=box-score]");
        await boxScore.WaitForAsync();
        Assert.Contains("NYY", await boxScore.InnerTextAsync());
        await player.CloseDialogAsync();

        await gameRecap.ClickAsync();
        var headline = player.Page.Locator("[data-testid=recap-headline]");
        await headline.WaitForAsync();
        Assert.False(string.IsNullOrWhiteSpace(await headline.InnerTextAsync()));
        await player.CloseDialogAsync();

        // the TV that was watching shows that the game is over
        await tv.Locator("[data-testid=tv-inning]").WaitForAsync();
        Assert.True(await GamePage.UntilAsync(async () =>
            (await tv.Locator("[data-testid=tv-inning]").InnerTextAsync()).Contains("FINAL"), seconds: 30));

        // a finished game is not kept to resume
        await player.Page.ReloadAsync();
        await player.Page.Locator("button", new() { HasText = "PLAY BALL" }).WaitForAsync();
        Assert.Equal(0, await player.Page.Locator("[data-testid=resume-card]").CountAsync());
        Assert.Empty(player.Errors);
    }
}
