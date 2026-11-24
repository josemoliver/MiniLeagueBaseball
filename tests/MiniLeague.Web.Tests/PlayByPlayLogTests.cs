using Bunit;
using MiniLeague.Engine.Models;
using MiniLeague.Web.Components.Game;

namespace MiniLeague.Web.Tests;

public class PlayByPlayLogTests : BunitContext
{
    [Fact]
    public void SearchInningAndLatestNavigateRecordedPlays()
    {
        PlayEvent[] plays =
        [
            new() { Inning = 2, IsTop = true, Description = "Ruth doubles to right.", VisitorScore = 1, HomeScore = 0 },
            new() { Inning = 1, IsTop = false, Description = "Snider flies out.", VisitorScore = 0, HomeScore = 0 },
            new() { Inning = 1, IsTop = true, Description = "Ruth walks.", VisitorScore = 0, HomeScore = 0 },
        ];
        var log = Render<PlayByPlayLog>(p => p.Add(x => x.Events, plays));

        Assert.Equal(3, log.FindAll(".pbp-entry").Count);
        Assert.Equal("Top 2", log.Find(".pbp-half").TextContent.Trim());

        log.Find("#pbp-search").Input("ruth");
        Assert.Equal(2, log.FindAll(".pbp-entry").Count);
        log.Find("#pbp-inning-filter").Change("1");
        Assert.Single(log.FindAll(".pbp-entry"));
        Assert.Contains("Ruth walks", log.Find(".pbp-entry").TextContent);

        log.Find(".pbp-latest").Click();
        Assert.Equal(3, log.FindAll(".pbp-entry").Count);
        Assert.Contains("Ruth doubles", log.Find(".pbp-entry").TextContent);
    }
}
