using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace MiniLeague.Web.E2E;

/// <summary>
/// Starts the real app once for all the browser tests (the same way the README says to run it) and a Chrome to
/// drive it. Uses the Chrome that is installed, so nothing has to be downloaded; set E2E_BROWSER_CHANNEL to use
/// another one (for example "msedge"), or to "bundled" for Playwright's own Chromium (run "playwright install" first).
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private Process? _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly List<string> _serverOutput = [];

    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        string root = FindRepoRoot();
        // WHY: 127.0.0.1 (like localhost) counts as a secure context, which the Presentation API needs; a LAN address would not.
        BaseUrl = $"http://127.0.0.1:{FreePort()}";

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // WHY: --no-build: building this test project already built the web app, and a second build here could clash
        // with that one over the same output files. NOTE: --environment Development is required or static assets 404.
        foreach (var arg in new[]
                 {
                     "run", "--no-build", "--project", "src/MiniLeague.Web", "--no-launch-profile",
                     "--", "--urls", BaseUrl, "--environment", "Development",
                 })
            start.ArgumentList.Add(arg);

        _server = Process.Start(start) ?? throw new InvalidOperationException("Could not start the app.");
        _server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_serverOutput) _serverOutput.Add(e.Data); };
        _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_serverOutput) _serverOutput.Add(e.Data); };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        await WaitForServerAsync();

        _playwright = await Playwright.CreateAsync();
        // WHY: the installed Chrome by default: nothing to download, and GitHub's Ubuntu runners already have it.
        string? channel = Environment.GetEnvironmentVariable("E2E_BROWSER_CHANNEL");
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Channel = channel == "bundled" ? null : channel ?? "chrome",
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        _server?.Dispose();
    }

    /// <summary>A fresh browser window (own storage) with the init script run in every page before the app starts.</summary>
    public async Task<IBrowserContext> NewContextAsync(int width, int height, string initScript)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        context.SetDefaultTimeout(60_000);   // the app is a WebAssembly download the first time
        await context.AddInitScriptAsync(initScript);
        return context;
    }

    private async Task WaitForServerAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int i = 0; i < 120; i++)
        {
            if (_server!.HasExited) throw new InvalidOperationException("The app stopped at start-up:\n" + Output());
            try
            {
                if ((await http.GetAsync(BaseUrl)).StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(1000);
        }
        throw new TimeoutException("The app did not start in two minutes:\n" + Output());
    }

    private string Output() { lock (_serverOutput) return string.Join('\n', _serverOutput.TakeLast(30)); }

    internal static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MiniLeagueBaseball.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root (MiniLeagueBaseball.slnx).");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition("app")]
public class AppCollection : ICollectionFixture<AppFixture>;
