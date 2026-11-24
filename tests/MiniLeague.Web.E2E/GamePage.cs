using Microsoft.Playwright;

namespace MiniLeague.Web.E2E;

/// <summary>One browser window on the game, with the steps the tests repeat. Collects any page error it sees.</summary>
public sealed class GamePage : IAsyncDisposable
{
    private readonly IBrowserContext _context;
    private readonly List<string> _errors = [];

    private GamePage(IBrowserContext context, IPage page, bool detailed)
    {
        _context = context;
        Page = page;
        Detailed = detailed;
        page.PageError += (_, message) => _errors.Add("page error: " + message);
        page.Console += (_, message) =>
        {
            // WHY: ignored on purpose: the development server has no hot-reload settings file, which the browser
            // logs as a console error on every page load. Any other console error fails the test that sees it.
            if (message.Type == "error" && !message.Text.Contains("hot-reload-settings") && !(message.Location ?? "").Contains("hot-reload-settings"))
                _errors.Add("console error: " + message.Text);
        };
    }

    public IPage Page { get; }
    public bool Detailed { get; }
    public IReadOnlyList<string> Errors => _errors;

    // ── Opening ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a page of the app. The view and (fast) animation speed are chosen up front, and sound is muted (unless
    /// <paramref name="muted"/> is false) so a run is quiet and does not wait for organ tunes. With
    /// <paramref name="setPreferences"/> false nothing is chosen, for tests of what the app remembers.
    /// <paramref name="savedGame"/> is a game to find in storage the first time the browser window opens, and
    /// <paramref name="script"/> is anything else to run before the app starts.
    /// </summary>
    public static async Task<GamePage> OpenAsync(AppFixture app, string path, bool detailed = false,
        int width = 1280, int height = 800, string script = "", bool muted = true, bool setPreferences = true,
        string? savedGame = null)
    {
        string view = detailed ? "detailed" : "classic";
        string preferences = setPreferences
            ? $$"""
                try {
                  localStorage.setItem('mlb-fieldview', '{{view}}');
                  localStorage.setItem('mlb-animspeed', 'fast');
                  localStorage.setItem('mlb-muted', '{{(muted ? "1" : "0")}}');
                } catch { }
                """
            : "";
        // WHY: seeded once per browser window, by a flag kept in the same storage. The init script runs in every page
        // of the window; a second tab (or a reload) must not put the saved game back after it has been played to its end.
        string seed = savedGame is null
            ? ""
            : $$"""
                try {
                  if (!localStorage.getItem('mlb-e2e-seeded')) {
                    localStorage.setItem('mlb-saved-game', {{System.Text.Json.JsonSerializer.Serialize(savedGame)}});
                    localStorage.setItem('mlb-e2e-seeded', '1');
                  }
                } catch { }
                """;
        var context = await app.NewContextAsync(width, height, preferences + seed + script);
        var page = await context.NewPageAsync();
        var player = new GamePage(context, page, detailed);
        await page.GotoAsync(app.BaseUrl + path);
        return player;
    }

    public async ValueTask DisposeAsync() => await _context.CloseAsync();

    /// <summary>Another tab in the same browser window (it shares storage and can talk to this one), opened on the TV view.</summary>
    public async Task<IPage> NewTabAsync(AppFixture app, string path)
    {
        var tab = await _context.NewPageAsync();
        tab.PageError += (_, message) => _errors.Add("page error (second tab): " + message);
        await tab.GotoAsync(app.BaseUrl + path);
        return tab;
    }

    // ── The game ─────────────────────────────────────────────────────────────

    private ILocator Button(string text) => Page.Locator("button", new PageLocatorOptions { HasText = text });

    /// <summary>Starts a game from the setup card. <paramref name="manage"/> is the "You manage" choice (default: the visitors).</summary>
    public async Task StartGameAsync(string? manage = null)
    {
        await Button("PLAY BALL").WaitForAsync();
        if (manage is not null)
        {
            await Page.Locator(".mud-select .mud-input-control").Nth(2).ClickAsync();
            await Page.Locator(".mud-list-item", new PageLocatorOptions { HasText = manage }).ClickAsync();
        }
        await Button("PLAY BALL").ClickAsync();
        await Page.Locator("[data-testid=field-region]").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
    }

    /// <summary>
    /// Closes the open dialog the way a player does (they do not close on Escape): with the close icon, or the Done
    /// button of the dialogs that have one instead. Waits until it is gone.
    /// </summary>
    public async Task CloseDialogAsync()
    {
        var dialog = Page.Locator(".mud-dialog");
        var closeIcon = dialog.Locator(".mud-button-close");
        if (await closeIcon.CountAsync() > 0)
            await closeIcon.First.ClickAsync();
        else
            await dialog.Locator("button", new LocatorLocatorOptions { HasText = "Done" }).ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    public Task<string> LastPlayAsync() =>
        Page.EvaluateAsync<string>("() => document.querySelector('.g-log .pbp-entry')?.innerText || ''");

    public Task<string> BatterAsync() =>
        Page.Locator("[data-testid=at-bat-name]").InnerTextAsync();

    public Task<bool> ErrorBannerShownAsync() => Page.Locator("#blazor-error-ui").IsVisibleAsync();

    /// <summary>Makes one pitch as the human manager and waits until its play (and any animation) is over.</summary>
    public async Task PitchAsync()
    {
        await WaitUntilIdleAsync();
        string before = await LastPlayAsync();
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Page.Locator(".decision-panel button", new PageLocatorOptions { HasText = "PITCH" }).ClickAsync();
            if (await UntilAsync(async () => await LastPlayAsync() != before, seconds: 10)) break;
        }
        await WaitUntilIdleAsync();
    }

    /// <summary>
    /// Waits until the animated field has nothing to show (the game's introduction, a play and the scenes after it
    /// play one after another, so it has to stay quiet for a moment). Does nothing in the Classic view.
    /// </summary>
    public async Task WaitUntilIdleAsync()
    {
        if (!Detailed) return;
        await Page.Locator("[data-testid=detailed-field]").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        // WHY: the introduction starts a moment after the field is mounted; checking for "quiet" straight away would
        // see it idle before it had begun.
        await UntilAsync(() => Page.EvaluateAsync<bool>("() => !!window.mlbAnimator && window.mlbAnimator.playing"), seconds: 3);
        DateTime quietSince = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await Page.EvaluateAsync<bool>("() => !!window.mlbAnimator && window.mlbAnimator.playing"))
                quietSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(1200))
                return;
            await Task.Delay(150);
        }
        throw new TimeoutException("The animated field never went quiet.");
    }

    // ── Casting (a pretend TV: Chrome cannot find a real one in a test) ──────

    /// <summary>Put in front of the app: a Presentation API that connects at once to a connection the test controls.</summary>
    // WHY: the fake extends the real PresentationRequest: cast.js assigns it to navigator.presentation.defaultRequest,
    // an attribute typed PresentationRequest, which rejects any other kind of object.
    public const string FakeCastSender = """
        (() => {
          window.__sent = [];
          const conn = { state: 'connected', send(t) { window.__sent.push(t); }, terminate() { this.state = 'terminated'; if (this.onterminate) this.onterminate(); } };
          window.__conn = conn;
          if (window.PresentationRequest) {
            const Real = window.PresentationRequest;
            window.PresentationRequest = class extends Real {
              getAvailability() { return Promise.resolve({ value: true }); }
              start() { return Promise.resolve(conn); }
            };
          }
        })();
        """;

    /// <summary>Put in front of the /tv page: it is handed a presentation connection the test controls, as a Cast screen would be.</summary>
    public const string FakeCastReceiver = """
        (() => {
          window.__sent = [];
          const conn = { state: 'connected', send(t) { window.__sent.push(t); } };
          window.__conn = conn;
          Object.defineProperty(navigator.presentation, 'receiver', {
            configurable: true,
            get: () => ({ connectionList: Promise.resolve({ connections: [conn] }) })
          });
        })();
        """;

    /// <summary>Counts the oscillators this page's own speakers are given, so a test can tell whether a sound was made here.</summary>
    // WHY: only AudioContext is counted, not OfflineAudioContext: sound.js plays "silenced" sounds on an offline context,
    // so a count that does not move is exactly how a test proves this device stayed quiet.
    public const string AudioCounter = """
        (() => {
          window.__audio = { oscillators: 0 };
          const Real = window.AudioContext;
          if (Real) window.AudioContext = class extends Real {
            createOscillator() { window.__audio.oscillators++; return super.createOscillator(); }
          };
        })();
        """;

    /// <summary>Clicks the Cast button, then (unless told not to) answers as the TV does when its page has opened.</summary>
    public async Task StartCastingAsync(bool tvSaysHello = true)
    {
        await Page.Locator("[data-testid=cast-button]").ClickAsync();
        await Page.WaitForFunctionAsync("() => document.querySelector('[data-testid=field-region]')?.className.includes('cast-away')");
        if (tvSaysHello) await TvSaysHelloAsync();
    }

    public Task TvSaysHelloAsync() => Page.EvaluateAsync("() => window.__conn.onmessage({ data: 'hello' })");

    /// <summary>The TV sends the page a message (for example that it cannot make sound).</summary>
    public Task TvSendsAsync(string json) => Page.EvaluateAsync("m => window.__conn.onmessage({ data: m })", json);

    public Task StopCastingAsync() => Page.EvaluateAsync("() => window.__conn.terminate()");

    /// <summary>Makes a sound the way the game does, and says how many oscillators this page's speakers have been given so far.</summary>
    public async Task<int> PlaySoundAsync(string name)
    {
        await Page.EvaluateAsync("n => window.mlbSound.play(n)", name);
        return await Page.EvaluateAsync<int>("() => window.__audio.oscillators");
    }

    /// <summary>Whether the latest game state sent to the TV is for the animated field.</summary>
    public async Task<bool?> LastStateIsDetailedAsync()
    {
        foreach (string message in (await SentToTvAsync()).Reverse())
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.GetProperty("k").GetString() == "state")
                return doc.RootElement.GetProperty("detailed").GetBoolean();
        }
        return null;
    }

    /// <summary>What the page has sent to the TV so far (JSON messages).</summary>
    public async Task<string[]> SentToTvAsync() =>
        await Page.EvaluateAsync<string[]>("() => window.__sent.filter(t => t !== 'hello')");

    public static string KindOf(string message) =>
        System.Text.Json.JsonDocument.Parse(message).RootElement.GetProperty("k").GetString() ?? "";

    // ── Waiting ──────────────────────────────────────────────────────────────

    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(250);
        }
        return false;
    }
}
