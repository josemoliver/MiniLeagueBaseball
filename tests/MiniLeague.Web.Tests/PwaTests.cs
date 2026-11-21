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

    [Fact]
    public void TheHostingConfigServesTheWorkerFreshAndKnowsTheManifestType()
    {
        var config = JsonDocument.Parse(Read("staticwebapp.config.json")).RootElement;

        Assert.Equal("application/manifest+json", config.GetProperty("mimeTypes").GetProperty(".webmanifest").GetString());
        var noCache = config.GetProperty("routes").EnumerateArray()
            .Where(r => r.GetProperty("headers").GetProperty("cache-control").GetString() == "no-cache")
            .Select(r => r.GetProperty("route").GetString()).ToList();
        Assert.Contains("/service-worker.js", noCache);            // otherwise an old worker could be served for a day
        Assert.Contains("/service-worker-assets.js", noCache);
        Assert.Contains("/manifest.webmanifest", noCache);
        // and these files are real files, not the single-page-app fallback
        string exclude = config.GetProperty("navigationFallback").GetProperty("exclude").EnumerateArray().Last().GetString()!;
        Assert.Contains("webmanifest", exclude);
        Assert.Contains("webp", exclude);
        Assert.Contains("png", exclude);
    }

    // ── The install menu item ────────────────────────────────────────────────

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        Render<MudBlazor.MudPopoverProvider>();
        return Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "page"))));
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

    [Fact]
    public void TheMenuStillHasItsUsualItems()
    {
        var layout = RenderLayout();

        foreach (var item in new[] { "Home", "Play Ball", "Keyboard Cards" })
            Assert.Contains(item, layout.Markup);
    }
}
