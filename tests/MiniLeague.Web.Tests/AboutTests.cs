using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MiniLeague.Web.Pages;
using MiniLeague.Web.Services;
using MudBlazor.Services;

namespace MiniLeague.Web.Tests;

/// <summary>The About page: the disclaimer and the credits the game has to show.</summary>
public class AboutTests : BunitContext, IAsyncLifetime
{
    public AboutTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void TheDisclaimerSaysTheGameIsNotAffiliatedWithBaseball()
    {
        var text = Render<About>().Find("[data-testid=about-disclaimer]").TextContent;

        Assert.Contains("independent fan project", text);
        Assert.Contains("not affiliated with, authorized by or endorsed by", text);
        Assert.Contains("Major League Baseball", text);
        Assert.Contains("Players Association", text);
        Assert.DoesNotContain("MicroLeague", text);
        Assert.Contains("trademarks", text);
    }

    [Fact]
    public void TheLahmanDatabaseIsCreditedUnderItsLicense_WithLinks()
    {
        var about = Render<About>();
        var data = about.Find("[data-testid=about-data]");

        Assert.Contains("Lahman Baseball Database", data.TextContent);
        Assert.Contains("Sean Lahman", data.TextContent);
        Assert.Contains("CC BY-SA 3.0", data.TextContent);
        Assert.Contains("same license", data.TextContent);
        Assert.Contains("approximate", data.TextContent);   // the ballpark distances are not from Lahman

        var links = data.QuerySelectorAll("a").ToDictionary(a => a.GetAttribute("href")!, a => a.GetAttribute("rel"));
        Assert.Contains("https://sabr.org/lahman-database/", links.Keys);
        Assert.Contains("https://creativecommons.org/licenses/by-sa/3.0/", links.Keys);
        Assert.All(links.Values, rel => Assert.Contains("noopener", rel));   // they open in a new tab
    }

    [Fact]
    public void TheMusicAndTheCodeAreCredited()
    {
        var about = Render<About>();

        Assert.Contains("public-domain", about.Find("[data-testid=about-music]").TextContent);
        var code = about.Find("[data-testid=about-code]");
        Assert.Contains("MIT License", code.TextContent);
        Assert.Contains("github.com/josemoliver/MiniLeagueBaseball", code.InnerHtml);
    }
}
