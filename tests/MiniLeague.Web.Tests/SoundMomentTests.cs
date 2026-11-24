using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MiniLeague.Engine.Models;
using MiniLeague.Web.Services;

namespace MiniLeague.Web.Tests;

/// <summary>Which organ tune plays for which moment of the game.</summary>
public class SoundMomentTests : BunitContext, IAsyncLifetime
{
    public SoundMomentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<SoundService>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private SoundService Sound => Services.GetRequiredService<SoundService>();

    private List<JSRuntimeInvocation> Plays() => JSInterop.Invocations["mlbSound.play"].ToList();

    [Theory]
    [InlineData(GameMoment.SeventhInningStretch, "stretch")]
    [InlineData(GameMoment.Rally, "rally")]
    [InlineData(GameMoment.PitchingChange, "entrance")]
    [InlineData(GameMoment.ManagerEjected, "funeral")]
    public async Task EachMomentHasItsOwnTune(GameMoment moment, string tune)
    {
        await Sound.PlayMomentsAsync([moment]);

        var play = Assert.Single(Plays());
        Assert.Equal(tune, play.Arguments[0]);
        Assert.Equal(1.0, play.Arguments[1]);   // a second after the play's own sound
    }

    [Fact]
    public void EveryMomentThatCanHappenHasADistinctTune()
    {
        var tunes = Enum.GetValues<GameMoment>().Select(SoundService.TuneFor).ToList();

        Assert.All(tunes, t => Assert.NotEmpty(t));
        Assert.Equal(tunes.Count, tunes.Distinct().Count());
    }

    [Fact]
    public async Task WhenSeveralHappenTogether_OnlyTheMostImportantPlays()
    {
        await Sound.PlayMomentsAsync([GameMoment.PitchingChange, GameMoment.Rally, GameMoment.SeventhInningStretch]);
        await Sound.PlayMomentsAsync([GameMoment.Rally, GameMoment.PitchingChange]);
        await Sound.PlayMomentsAsync([GameMoment.Rally, GameMoment.ManagerEjected]);

        // one tune per call: the stretch beats a rally beats a change, and an ejection beats everything
        Assert.Equal(["stretch", "rally", "funeral"], Plays().Select(p => (string?)p.Arguments[0]));
    }

    [Fact]
    public async Task NoMoments_NoSound()
    {
        await Sound.PlayMomentsAsync([]);

        Assert.Empty(JSInterop.Invocations["mlbSound.play"]);
    }

    [Fact]
    public async Task IfTheBrowserCannotPlayAudio_NothingBreaks()
    {
        JSInterop.SetupVoid("mlbSound.play", _ => true).SetException(new JSException("no audio"));

        await Sound.PlayMomentsAsync([GameMoment.Rally]);   // does not throw
    }
}
