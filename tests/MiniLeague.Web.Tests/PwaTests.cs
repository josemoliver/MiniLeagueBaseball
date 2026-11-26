using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MiniLeague.Web.Services;
using MiniLeague.Web.Shared;
using MudBlazor.Services;

namespace MiniLeague.Web.Tests;

/// <summary>Installing the game as an app: the manifest, icons, page setup, offline files and the install menu item.</summary>
public class PwaTests : BunitContext, IAsyncLifetime
{
    private static readonly string WwwRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "MiniLeague.Web", "wwwroot"));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(WwwRoot, relative));

    public PwaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddScoped<SoundService>();
        Services.AddScoped<CastService>();
        Services.AddScoped<PwaService>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    // ── The manifest ─────────────────────────────────────────────────────────

    private static JsonElement Manifest() => JsonDocument.Parse(Read("manifest.webmanifest")).RootElement;

    [Fact]
    public void TheManifestDescribesAStandaloneAppThatOpensAtTheStart()
    {
        var m = Manifest();

        Assert.Equal("MiniLeague Baseball", m.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("short_name").GetString()));
        Assert.True(m.GetProperty("short_name").GetString()!.Length <= 12);   // fits under a home-screen icon
        Assert.Equal("/", m.GetProperty("start_url").GetString());
        Assert.Equal("/", m.GetProperty("scope").GetString());
        Assert.Equal("standalone", m.GetProperty("display").GetString());
        Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("description").GetString()));
    }

    [Fact]
    public void TheManifestColorsMatchTheArtworkBlue()
    {
        var m = Manifest();

        Assert.Equal(AppTheme.ArtworkBlue, m.GetProperty("theme_color").GetString());
        Assert.Equal(AppTheme.ArtworkBlue, m.GetProperty("background_color").GetString());
        Assert.Contains(AppTheme.ArtworkBlue, Read("index.html"));   // the browser chrome color on the page itself
    }

    private static (int Width, int Height) PngSize(string relative)
    {
        var bytes = File.ReadAllBytes(Path.Combine(WwwRoot, relative));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);   // a real PNG
        int Read(int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
        return (Read(16), Read(20));   // the IHDR chunk
    }

    [Fact]
    public void EveryIconExists_IsAPngOfTheSizeItClaims_AndThereAreMaskableOnes()
    {
        var icons = Manifest().GetProperty("icons").EnumerateArray().ToList();

        foreach (var icon in icons)
        {
            string size = icon.GetProperty("sizes").GetString()!;
            var (w, h) = PngSize(icon.GetProperty("src").GetString()!);
            Assert.Equal(size, $"{w}x{h}");
            Assert.Equal("image/png", icon.GetProperty("type").GetString());
        }

        var big = icons.Where(i => i.GetProperty("sizes").GetString() == "512x512").ToList();
        Assert.Contains(big, i => i.GetProperty("purpose").GetString() == "any");
        Assert.Contains(big, i => i.GetProperty("purpose").GetString() == "maskable");   // required for a proper Android icon
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "192x192");
        Assert.Equal((180, 180), PngSize("icons/apple-touch-icon.png"));
    }

    [Fact]
    public void TheMaskableIconKeepsItsPictureInsideTheSafeZone()
    {
        // The corners are solid brand blue, so a circular or rounded mask cuts only blue, never the artwork.
        var bytes = File.ReadAllBytes(Path.Combine(WwwRoot, "icons", "icon-maskable-512.png"));

        Assert.True(bytes.Length < 600_000, "an icon should be small");
        Assert.Equal((512, 512), PngSize("icons/icon-maskable-512.png"));
    }

    [Fact]
    public void TheShortcutsPointAtRealPages()
    {
        var urls = Manifest().GetProperty("shortcuts").EnumerateArray().Select(s => s.GetProperty("url").GetString()).ToList();

        Assert.Contains("/game", urls);
        Assert.Contains("/shortcuts", urls);
    }

    // ── The page ─────────────────────────────────────────────────────────────

    [Fact]
    public void ThePageLinksTheManifestIconsAndInstallScript_AndFillsAPhonesScreen()
    {
        string html = Read("index.html");

        Assert.Contains("<link rel=\"manifest\" href=\"manifest.webmanifest\"", html);
        Assert.Contains("rel=\"apple-touch-icon\"", html);
        Assert.Contains("name=\"theme-color\"", html);
        Assert.Contains("viewport-fit=cover", html);                    // use the whole screen on a notched phone
        Assert.Contains("apple-mobile-web-app-capable", html);
        Assert.Contains("<script src=\"js/pwa.js\"></script>", html);
        Assert.Contains("id=\"update-banner\"", html);
        Assert.Contains("hidden", html[html.IndexOf("id=\"update-banner\"", StringComparison.Ordinal)..][..40]);   // hidden until needed
    }

    [Fact]
    public void TheInstalledLayoutKeepsTheMenuBelowTheIphoneStatusBar()
    {
        string css = Read("css/site.css");
        string gameCss = File.ReadAllText(Path.Combine(WwwRoot, "..", "Pages", "GameRoom.razor.css"));
        var layout = RenderLayout();

        Assert.NotNull(layout.Find(".pwa-app-bar"));
        Assert.NotNull(layout.Find(".pwa-main-content"));
        Assert.Contains(".pwa-app-bar", css);
        Assert.Contains("padding-top: env(safe-area-inset-top)", css);
        Assert.Contains(".mud-appbar-dense ~ .pwa-main-content", css);
        Assert.Contains("+ env(safe-area-inset-top)", css);
        Assert.Contains("top: calc(var(--mud-appbar-height)", gameCss);
        Assert.Contains("+ env(safe-area-inset-top)", gameCss);
    }

    [Fact]
    public void ThePwaScriptLoadsBeforeTheAppStarts()
    {
        string html = Read("index.html");

        Assert.True(html.IndexOf("js/pwa.js", StringComparison.Ordinal) < html.IndexOf("_framework/blazor.webassembly.js", StringComparison.Ordinal));
    }

    // ── Offline files ────────────────────────────────────────────────────────

    [Fact]
    public void TheProjectBuildsAnOfflineAssetListAndSwapsInTheRealWorkerOnPublish()
    {
        string csproj = File.ReadAllText(Path.Combine(WwwRoot, "..", "MiniLeague.Web.csproj"));

        Assert.Contains("<ServiceWorkerAssetsManifest>service-worker-assets.js</ServiceWorkerAssetsManifest>", csproj);
        Assert.Contains("ServiceWorker Include=\"wwwroot\\service-worker.js\" PublishedContent=\"wwwroot\\service-worker.published.js\"", csproj);
        Assert.True(File.Exists(Path.Combine(WwwRoot, "service-worker.js")));
        Assert.True(File.Exists(Path.Combine(WwwRoot, "service-worker.published.js")));
    }

    [Fact]
    public void TheDevelopmentWorkerNeverCaches_SoYouAlwaysSeeYourLatestBuild()
    {
        string dev = Read("service-worker.js");

        Assert.DoesNotContain("caches.", dev);
        Assert.DoesNotContain("importScripts", dev);
    }

    [Fact]
    public void ThePublishedWorkerLoadsTheAssetListAndTakesOverOnlyOnRequest()
    {
        string published = Read("service-worker.published.js");

        Assert.Contains("importScripts('./service-worker-assets.js')", published);
        Assert.Contains("SKIP_WAITING", published);
        Assert.Contains(".webp", published);                       // the artwork works offline
        Assert.Contains("webmanifest", published);
        Assert.DoesNotContain("self.skipWaiting();\n", published.Split("addEventListener('message'")[0]);   // no automatic takeover on install
    }

    // ── The install menu item ────────────────────────────────────────────────

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        Render<MudBlazor.MudPopoverProvider>();
        return Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "page"))));
    }

    [Fact]
    public void TheMenuLinksToTheAboutPage()
    {
        var layout = RenderLayout();
        OpenMenu(layout);

        var link = layout.Find("[data-testid=about-link] a");

        Assert.Equal("about", link.GetAttribute("href"));
        Assert.Contains("About", link.TextContent);
    }

    // ── The TV view menu item: only once casting has failed ─────────────────

    [Fact]
    public void TheTvViewIsNotInTheMenuWhileCastingIsAvailable()
    {
        JSInterop.Setup<bool>("mlbCast.init", _ => true).SetResult(true);
        var layout = RenderLayout();
        OpenMenu(layout);

        layout.WaitForAssertion(() => Assert.Single(layout.FindAll("[data-testid=cast-button]")));
        Assert.Empty(layout.FindAll("[data-testid=tv-link]"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("none")]
    public async Task TheTvViewAppearsInTheMenuAfterCastingFails_AndGoesWhenItWorks(string outcome)
    {
        JSInterop.Setup<bool>("mlbCast.init", _ => true).SetResult(true);
        var layout = RenderLayout();
        OpenMenu(layout);
        layout.WaitForAssertion(() => Assert.Single(layout.FindAll("[data-testid=cast-button]")));
        var cast = Services.GetRequiredService<CastService>();

        JSInterop.Setup<string>("mlbCast.start").SetResult(outcome);
        await layout.InvokeAsync(() => cast.StartAsync());
        Assert.Equal("tv", layout.Find("[data-testid=tv-link] a").GetAttribute("href"));

        JSInterop.Setup<string>("mlbCast.start").SetResult("started");
        await layout.InvokeAsync(() => cast.StartAsync());
        Assert.Empty(layout.FindAll("[data-testid=tv-link]"));
    }

    [Fact]
    public async Task ACancelledPickerIsNotAFailure()
    {
        JSInterop.Setup<bool>("mlbCast.init", _ => true).SetResult(true);
        var layout = RenderLayout();
        OpenMenu(layout);
        layout.WaitForAssertion(() => Assert.Single(layout.FindAll("[data-testid=cast-button]")));
        JSInterop.Setup<string>("mlbCast.start").SetResult("cancelled");

        await layout.InvokeAsync(() => Services.GetRequiredService<CastService>().StartAsync());

        Assert.Empty(layout.FindAll("[data-testid=tv-link]"));
    }

    [Fact]
    public void TheTvViewIsInTheMenuWhenTheBrowserCannotCast()
    {
        JSInterop.Setup<bool>("mlbCast.init", _ => true).SetResult(false);
        var layout = RenderLayout();
        OpenMenu(layout);

        layout.WaitForAssertion(() => Assert.Single(layout.FindAll("[data-testid=tv-link]")));
    }

    private void OpenMenu(IRenderedComponent<MainLayout> layout) =>
        layout.FindAll("button").First(b => b.QuerySelector(".mud-icon-root") is not null).Click();

    [Fact]
    public async Task ThePwaService_ReportsWhetherInstallingIsPossible_AndTheOutcome()
    {
        var pwa = Services.GetRequiredService<PwaService>();

        JSInterop.Setup<bool>("mlbPwa.canInstall").SetResult(true);
        Assert.True(await pwa.CanInstallAsync());

        JSInterop.Setup<string>("mlbPwa.install").SetResult("accepted");
        Assert.Equal("accepted", await pwa.InstallAsync());
    }

    [Fact]
    public async Task IfTheBrowserHasNoInstallSupport_NothingBreaks()
    {
        JSInterop.Setup<bool>("mlbPwa.canInstall").SetException(new JSException("not available"));
        JSInterop.Setup<string>("mlbPwa.install").SetException(new JSException("not available"));
        var pwa = Services.GetRequiredService<PwaService>();

        Assert.False(await pwa.CanInstallAsync());
        Assert.Equal("unavailable", await pwa.InstallAsync());
    }

    [Fact]
    public void TheMenuOffersInstallOnlyWhenTheBrowserAllowsIt()
    {
        JSInterop.Setup<bool>("mlbPwa.canInstall").SetResult(false);
        var without = RenderLayout();
        OpenMenu(without);
        Assert.Empty(without.FindAll("[data-testid=install-app]"));
    }

    [Fact]
    public void TheMenuShowsInstallApp_AndChoosingItShowsTheBrowserPrompt()
    {
        JSInterop.Setup<bool>("mlbPwa.canInstall").SetResult(true);
        JSInterop.Setup<string>("mlbPwa.install").SetResult("accepted");
        var layout = RenderLayout();

        OpenMenu(layout);
        layout.WaitForState(() => layout.FindAll("[data-testid=install-app]").Count > 0);
        layout.Find("[data-testid=install-app] .mud-nav-link").Click();

        JSInterop.VerifyInvoke("mlbPwa.install");
        Assert.Contains("Install app", layout.Markup);
    }

    private static string ProjectVersion(string project, string file) =>
        System.Xml.Linq.XDocument.Load(Path.GetFullPath(Path.Combine(WwwRoot, "..", "..", project, file)))
            .Descendants("Version").Single().Value;

    [Fact]
    public void TheMenuEndsWithTheVersionOfTheApp_AsTheProjectFileSaysIt()
    {
        var layout = RenderLayout();
        OpenMenu(layout);

        var version = layout.Find("[data-testid=app-version]");
        string expected = ProjectVersion("MiniLeague.Web", "MiniLeague.Web.csproj");
        Assert.Equal($"Version {expected}", version.TextContent.Trim());
        Assert.Matches(@"^\d+\.\d+\.\d+$", expected);

        // After every item of the menu, not before or among them.
        Assert.True(layout.Markup.LastIndexOf("mud-nav-link") < layout.Markup.IndexOf("data-testid=\"app-version\""));
    }

    [Fact]
    public void TheEngineAndTheAppAreTheSameVersion()
    {
        Assert.Equal(ProjectVersion("MiniLeague.Web", "MiniLeague.Web.csproj"),
                     ProjectVersion("MiniLeague.Engine", "MiniLeague.Engine.csproj"));
    }

    [Fact]
    public void TheMenuStillHasItsUsualItems()
    {
        var layout = RenderLayout();

        foreach (var item in new[] { "Home", "Play Ball", "Keyboard Cards" })
            Assert.Contains(item, layout.Markup);
    }
}
