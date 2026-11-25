using System.Text.Json;
using Microsoft.Playwright;

namespace MiniLeague.Web.E2E;

/// <summary>What happens around a Cast session when things change: the TV and the sound, stopping, a late TV, a new view.</summary>
[Collection("app")]
public class CastEdgeCaseTests(AppFixture app)
{
    private static int SoundMessages(string[] sent) =>
        sent.Count(m => GamePage.KindOf(m) == "sound");

    [Fact]
    public async Task TheTvPlaysTheSounds_AndThisDeviceStaysQuietUntilTheTvStopsOrCannot()
    {
        await using var player = await GamePage.OpenAsync(app, "/game",
            script: GamePage.FakeCastSender + GamePage.AudioCounter, muted: false);
        await player.StartGameAsync();
        await Task.Delay(1500);   // the game's own start-up sound is over

        // casting, but the TV has not said it is there yet: sound stays here
        await player.StartCastingAsync(tvSaysHello: false);
        int heard = await player.PlaySoundAsync("walk");
        Assert.True(heard > 0, "no sound was made on this device");
        Assert.Equal(0, SoundMessages(await player.SentToTvAsync()));

        // the TV is there: it is sent the sound, and this device makes none
        await player.TvSaysHelloAsync();
        int quiet = await player.PlaySoundAsync("walk");
        Assert.Equal(heard, quiet);
        Assert.Equal(1, SoundMessages(await player.SentToTvAsync()));

        // the TV says the browser will not let it make sound: it comes back here, and nothing more is sent
        await player.TvSendsAsync("""{"k":"nosound"}""");
        int back = await player.PlaySoundAsync("walk");
        Assert.True(back > quiet, "the sound did not come back to this device");
        Assert.Equal(1, SoundMessages(await player.SentToTvAsync()));

        // and the same when casting simply ends
        await player.TvSaysHelloAsync();
        Assert.Equal(back, await player.PlaySoundAsync("walk"));
        await player.StopCastingAsync();
        Assert.True(await player.PlaySoundAsync("walk") > back, "the sound did not come back when casting ended");
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task StoppingTheCastInTheMiddleOfAPlayLeavesTheGamePlayable()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: true, script: GamePage.FakeCastSender);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        await player.StartCastingAsync();

        await player.Page.Locator(".decision-panel button", new() { HasText = "PITCH" }).ClickAsync();   // the play starts...
        await player.StopCastingAsync();                                                  // ...and the TV goes away

        Assert.True(await GamePage.UntilAsync(async () =>
            !(await player.Page.GetAttributeAsync("[data-testid=field-region]", "class"))!.Contains("cast-away"), seconds: 40));
        await player.PitchAsync();   // still playable, with the field back
        Assert.True(await player.Page.Locator("[data-testid=detailed-field]").IsVisibleAsync());
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }

    [Fact]
    public async Task ATvThatJoinsLateIsBroughtUpToDate()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", script: GamePage.FakeCastSender);
        await player.StartGameAsync();
        await player.StartCastingAsync();
        await player.PitchAsync();
        string[] before = await player.SentToTvAsync();

        await player.TvSaysHelloAsync();   // a second TV page opens and asks for the game

        string[] after = await player.SentToTvAsync();
        Assert.True(after.Length > before.Length, "nothing was sent to the new TV page");
        Assert.Equal("state", GamePage.KindOf(after[^1]));
        // and it is the game as it stands now, the same as the last one sent
        Assert.Equal(JsonDocument.Parse(before[^1]).RootElement.GetProperty("snapshot").GetString(),
                     JsonDocument.Parse(after[^1]).RootElement.GetProperty("snapshot").GetString());
    }

    [Fact]
    public async Task SwitchingTheFieldViewWhileCastingChangesWhatTheTvShows()
    {
        await using var player = await GamePage.OpenAsync(app, "/game", detailed: true, script: GamePage.FakeCastSender);
        await player.StartGameAsync();
        await player.WaitUntilIdleAsync();
        await player.StartCastingAsync();
        Assert.True(await GamePage.UntilAsync(async () => await player.LastStateIsDetailedAsync() == true, seconds: 10));

        await player.Page.Locator("[data-testid=view-classic]").ClickAsync();
        Assert.True(await GamePage.UntilAsync(async () => await player.LastStateIsDetailedAsync() == false, seconds: 10),
            "the TV was not told to show the Classic field");

        await player.Page.Locator("[data-testid=view-detailed]").ClickAsync();
        Assert.True(await GamePage.UntilAsync(async () => await player.LastStateIsDetailedAsync() == true, seconds: 10),
            "the TV was not told to show the animated field");
        Assert.True(await player.Page.GetAttributeAsync("[data-testid=field-region]", "class") is { } c && c.Contains("cast-away"));
        Assert.False(await player.ErrorBannerShownAsync());
        Assert.Empty(player.Errors);
    }
}
