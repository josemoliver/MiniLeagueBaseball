using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MiniLeague.Engine.Models;
using MiniLeague.Web.Components.Game;
using MiniLeague.Web.Pages;
using MiniLeague.Web.Services;
using MiniLeague.Web.Shared;
using MudBlazor.Services;

namespace MiniLeague.Web.Tests;

/// <summary>The keyboard shortcuts: the key map, the live keys in the decision panel, and the printable cards.</summary>
public class ShortcutTests : BunitContext, IAsyncLifetime
{
    public ShortcutTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddScoped<ExportService>();
        Services.AddScoped<SoundService>();
        Services.AddScoped<CastService>();
        Services.AddScoped<PwaService>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    // ── The key map ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Digit1", OffensiveStrategy.SwingAway)]
    [InlineData("Digit2", OffensiveStrategy.SwingAgressive)]
    [InlineData("Digit3", OffensiveStrategy.SwingSafe)]
    [InlineData("Digit4", OffensiveStrategy.Steal)]
    [InlineData("Digit5", OffensiveStrategy.HitAndRun)]
    [InlineData("Digit6", OffensiveStrategy.SacrificeBunt)]
    [InlineData("Numpad4", OffensiveStrategy.Steal)]    // the number pad works too
    public void OnOffense_NumberKeysPickTheStrategy(string code, OffensiveStrategy expected)
    {
        var action = Shortcuts.Resolve(code, shift: false, offense: true, defense: false);

        Assert.Equal((ShortcutKind.Offense, expected), (action.Kind, action.Strategy));
    }

    [Theory]
    [InlineData("Digit1", PitchType.Fastball)]
    [InlineData("Digit2", PitchType.Curveball)]
    [InlineData("Digit3", PitchType.Slider)]
    [InlineData("Digit4", PitchType.Changeup)]
    public void OnDefense_NumberKeysPickThePitch(string code, PitchType expected)
    {
        var action = Shortcuts.Resolve(code, shift: false, offense: false, defense: true);

        Assert.Equal((ShortcutKind.Pitch, expected), (action.Kind, action.Pitch));
    }

    [Theory]
    [InlineData("Digit5", DefensiveAlignment.Standard)]
    [InlineData("Digit6", DefensiveAlignment.InfieldIn)]
    [InlineData("Digit7", DefensiveAlignment.CornersIn)]
    [InlineData("Digit8", DefensiveAlignment.GuardLines)]
    public void OnDefense_FiveToEightPickTheAlignment(string code, DefensiveAlignment expected)
    {
        var action = Shortcuts.Resolve(code, shift: false, offense: false, defense: true);

        Assert.Equal((ShortcutKind.Alignment, expected), (action.Kind, action.Alignment));
    }

    [Theory]
    [InlineData("Digit0", DefensiveAlignment.ShiftPull)]
    [InlineData("Numpad0", DefensiveAlignment.ShiftPull)]
    [InlineData("Minus", DefensiveAlignment.ShiftOpposite)]
    [InlineData("NumpadSubtract", DefensiveAlignment.ShiftOpposite)]
    public void OnDefense_ZeroAndMinusPickTheShifts(string code, DefensiveAlignment expected)
    {
        var action = Shortcuts.Resolve(code, shift: false, offense: false, defense: true);

        Assert.Equal((ShortcutKind.Alignment, expected), (action.Kind, action.Alignment));
        // not on offense, and with both teams managed they need Shift like the other defense keys
        Assert.Equal(ShortcutKind.None, Shortcuts.Resolve(code, false, offense: true, defense: false).Kind);
        Assert.Equal(ShortcutKind.None, Shortcuts.Resolve(code, false, offense: true, defense: true).Kind);
        Assert.Equal(expected, Shortcuts.Resolve(code, true, offense: true, defense: true).Alignment);
    }

    [Fact]
    public void OnDefense_NineIsTheIntentionalWalk()
    {
        Assert.Equal(ShortcutKind.IntentionalWalk, Shortcuts.Resolve("Digit9", false, false, true).Kind);
        Assert.Equal(ShortcutKind.None, Shortcuts.Resolve("Digit9", false, true, false).Kind);   // not on offense
    }

    [Fact]
    public void WhenOnePersonManagesBothTeams_PlainNumbersAreTheOffenseAndShiftIsTheDefense()
    {
        var offense = Shortcuts.Resolve("Digit2", shift: false, offense: true, defense: true);
        var defense = Shortcuts.Resolve("Digit2", shift: true, offense: true, defense: true);

        Assert.Equal((ShortcutKind.Offense, OffensiveStrategy.SwingAgressive), (offense.Kind, offense.Strategy));
        Assert.Equal((ShortcutKind.Pitch, PitchType.Curveball), (defense.Kind, defense.Pitch));
    }

    [Theory]
    [InlineData("Enter", ShortcutKind.Play)]
    [InlineData("NumpadEnter", ShortcutKind.Play)]
    [InlineData("KeyP", ShortcutKind.Play)]
    [InlineData("KeyS", ShortcutKind.Substitutions)]
    [InlineData("KeyB", ShortcutKind.BoxScore)]
    [InlineData("KeyZ", ShortcutKind.None)]
    [InlineData("Digit0", ShortcutKind.None)]
    [InlineData("Digit9", ShortcutKind.None)]    // no ninth offensive strategy
    public void GameKeys_AndKeysThatDoNothing(string code, ShortcutKind expected)
    {
        Assert.Equal(expected, Shortcuts.Resolve(code, false, offense: true, defense: false).Kind);
    }

    [Fact]
    public void WithNobodyToManage_NoKeyDoesAnything()
    {
        Assert.Equal(ShortcutKind.None, Shortcuts.Resolve("Enter", false, false, false).Kind);
        Assert.Equal(ShortcutKind.None, Shortcuts.Resolve("Digit1", false, false, false).Kind);
    }

    [Fact]
    public void NoKeyIsUsedTwice_AndEveryChoiceHasOne()
    {
        Assert.Equal(Shortcuts.OffenseKeys.Length, Shortcuts.OffenseKeys.Select(k => k.Key).Distinct().Count());

        var defense = Shortcuts.PitchKeys.Select(k => k.Key)
            .Concat(Shortcuts.AlignmentKeys.Select(k => k.Key))
            .Append(Shortcuts.IntentionalWalkKey).ToList();
        Assert.Equal(defense.Count, defense.Distinct().Count());

        // everything the panel offers has a key
        foreach (var strategy in new[] { OffensiveStrategy.SwingAway, OffensiveStrategy.SwingAgressive, OffensiveStrategy.SwingSafe,
                                         OffensiveStrategy.Steal, OffensiveStrategy.HitAndRun, OffensiveStrategy.SacrificeBunt })
            Assert.NotEqual("", Shortcuts.KeyFor(strategy));
        foreach (var pitch in new[] { PitchType.Fastball, PitchType.Curveball, PitchType.Slider, PitchType.Changeup })
            Assert.NotEqual("", Shortcuts.KeyFor(pitch));
        foreach (var alignment in new[] { DefensiveAlignment.Standard, DefensiveAlignment.InfieldIn,
                                          DefensiveAlignment.CornersIn, DefensiveAlignment.GuardLines,
                                          DefensiveAlignment.ShiftPull, DefensiveAlignment.ShiftOpposite })
            Assert.NotEqual("", Shortcuts.KeyFor(alignment));
    }

    // ── The decision panel ───────────────────────────────────────────────────

    private (IRenderedComponent<DecisionPanel> Panel, List<(OffensiveDecision? O, DefensiveDecision? D)> Decisions, int[] Counts)
        RenderPanel(bool offense, bool defense, bool canSteal = false)
    {
        var decisions = new List<(OffensiveDecision?, DefensiveDecision?)>();
        var counts = new int[2];   // substitutions, box score
        var panel = Render<DecisionPanel>(p => p
            .Add(x => x.ShowOffense, offense)
            .Add(x => x.ShowDefense, defense)
            .Add(x => x.CanSteal, canSteal)
            .Add(x => x.OnDecision, EventCallback.Factory.Create<(OffensiveDecision?, DefensiveDecision?)>(this, d => decisions.Add(d)))
            .Add(x => x.OnSubstitutions, EventCallback.Factory.Create(this, () => counts[0]++))
            .Add(x => x.OnBoxScore, EventCallback.Factory.Create(this, () => counts[1]++)));
        return (panel, decisions, counts);
    }

    private static Task Press(IRenderedComponent<DecisionPanel> panel, string code, bool shift = false) =>
        panel.InvokeAsync(() => panel.Instance.OnKey(code, shift));

    [Fact]
    public async Task OnOffense_KeysChooseTheStrategyAndEnterPlaysIt()
    {
        var (panel, decisions, _) = RenderPanel(offense: true, defense: false, canSteal: true);

        await Press(panel, "Digit4");
        await Press(panel, "Enter");

        var decision = Assert.Single(decisions);
        Assert.Equal(OffensiveStrategy.Steal, decision.O!.Strategy);
        Assert.Null(decision.D);
    }

    [Fact]
    public async Task StealButtonAndShortcutFollowEligibility()
    {
        var (panel, decisions, _) = RenderPanel(offense: true, defense: false);
        var steal = panel.FindAll("button").Single(b => b.TextContent.Contains("Steal"));
        Assert.True(steal.HasAttribute("disabled"));

        await Press(panel, "Digit4");
        await Press(panel, "Enter");
        Assert.Equal(OffensiveStrategy.SwingAway, Assert.Single(decisions).O!.Strategy);

        panel.Render(p => p.Add(x => x.ShowOffense, true).Add(x => x.CanSteal, true));
        steal = panel.FindAll("button").Single(b => b.TextContent.Contains("Steal"));
        Assert.False(steal.HasAttribute("disabled"));

        await Press(panel, "Digit4");
        panel.Render(p => p.Add(x => x.ShowOffense, true).Add(x => x.CanSteal, false));
        var selected = panel.FindAll("button.mud-button-filled").Select(b => b.TextContent).ToList();
        Assert.Contains(selected, text => text.Contains("Swing Away"));
        Assert.DoesNotContain(selected, text => text.Contains("Steal"));
    }

    [Fact]
    public async Task HitAndRunNeedsARunnerOnFirstWithSecondOpen_LikeASteal_ButSacrificeAlwaysDoes()
    {
        var (panel, decisions, _) = RenderPanel(offense: true, defense: false);
        Button(panel, "Hit & Run", disabled: true);
        Button(panel, "Sacrifice", disabled: false);          // a bunt for a hit is allowed with the bases empty

        await Press(panel, "Digit5");                          // hit and run: ignored while it cannot be on
        await Press(panel, "Enter");
        Assert.Equal(OffensiveStrategy.SwingAway, Assert.Single(decisions).O!.Strategy);

        panel.Render(p => p.Add(x => x.ShowOffense, true).Add(x => x.CanSteal, true));
        Button(panel, "Hit & Run", disabled: false);
        await Press(panel, "Digit5");
        panel.Render(p => p.Add(x => x.ShowOffense, true).Add(x => x.CanSteal, false));   // the runner is gone: back to the default
        var selected = panel.FindAll("button.mud-button-filled").Select(b => b.TextContent).ToList();
        Assert.DoesNotContain(selected, text => text.Contains("Hit & Run"));

        await Press(panel, "Digit6");
        await Press(panel, "Enter");
        Assert.Equal(OffensiveStrategy.SacrificeBunt, decisions[^1].O!.Strategy);
    }

    private static void Button(IRenderedComponent<DecisionPanel> panel, string text, bool disabled) =>
        Assert.Equal(disabled, panel.FindAll("button").Single(b => b.TextContent.Contains(text)).HasAttribute("disabled"));

    [Fact]
    public async Task OnDefense_KeysChoosePitchAndAlignment_AndPPlays()
    {
        var (panel, decisions, _) = RenderPanel(offense: false, defense: true);

        await Press(panel, "Digit3");   // slider
        await Press(panel, "Digit6");   // infield in
        await Press(panel, "KeyP");

        var decision = Assert.Single(decisions).D!;
        Assert.Equal((PitchType.Slider, DefensiveAlignment.InfieldIn, false), (decision.PitchType, decision.Alignment, decision.IntentionalWalk));
    }

    [Fact]
    public async Task OnDefense_NineIssuesAnIntentionalWalkAtOnce()
    {
        var (panel, decisions, _) = RenderPanel(offense: false, defense: true);

        await Press(panel, "Digit9");

        Assert.True(Assert.Single(decisions).D!.IntentionalWalk);
    }

    [Fact]
    public async Task ManagingBothTeams_OneEnterSendsBothDecisions()
    {
        // a hit and run needs a runner on first and second open, like a steal
        var (panel, decisions, _) = RenderPanel(offense: true, defense: true, canSteal: true);

        await Press(panel, "Digit5");                  // hit and run
        await Press(panel, "Digit4", shift: true);     // changeup
        await Press(panel, "Enter");

        var decision = Assert.Single(decisions);
        Assert.Equal(OffensiveStrategy.HitAndRun, decision.O!.Strategy);
        Assert.Equal(PitchType.Changeup, decision.D!.PitchType);
    }

    [Fact]
    public async Task SAndB_OpenSubstitutionsAndTheBoxScore()
    {
        var (panel, _, counts) = RenderPanel(offense: true, defense: false);

        await Press(panel, "KeyS");
        await Press(panel, "KeyB");
        await Press(panel, "KeyB");

        Assert.Equal((1, 2), (counts[0], counts[1]));
    }

    [Fact]
    public async Task KeysThatMeanNothing_DoNothing()
    {
        var (panel, decisions, counts) = RenderPanel(offense: true, defense: false);

        await Press(panel, "KeyZ");
        await Press(panel, "Digit9");   // no ninth offensive strategy

        Assert.Empty(decisions);
        Assert.Equal((0, 0), (counts[0], counts[1]));
    }

    [Fact]
    public async Task PressingAKeyHighlightsTheChoice()
    {
        var (panel, _, _) = RenderPanel(offense: true, defense: false);

        await Press(panel, "Digit6");

        var selected = panel.FindAll("button.mud-button-filled").Select(b => b.TextContent).ToList();
        Assert.Contains(selected, t => t.Contains("Sacrifice"));
        Assert.DoesNotContain(selected, t => t.Contains("Swing Away"));
    }

    [Fact]
    public void TheButtonsShowTheirKeys_AndTheDefenseShowsShiftWhenBothSidesAreManaged()
    {
        var single = RenderPanel(offense: false, defense: true).Panel;
        Assert.Contains("Enter", single.Markup);
        var hints = single.FindAll(".key-hint").Select(h => h.TextContent).ToList();
        Assert.Contains("1", hints);
        Assert.Contains("9", hints);
        Assert.DoesNotContain(hints, h => h.StartsWith('⇧'));

        var both = RenderPanel(offense: true, defense: true).Panel;
        var bothHints = both.FindAll(".key-hint").Select(h => h.TextContent).ToList();
        Assert.Contains("⇧1", bothHints);
        Assert.Contains("⇧9", bothHints);
        Assert.Contains("4", bothHints);   // the offense keeps plain numbers
    }

    [Fact]
    public async Task ThePanelListensForKeysWhileItIsOnScreen_AndStopsWhenItGoesAway()
    {
        var (panel, _, _) = RenderPanel(offense: true, defense: false);
        JSInterop.VerifyInvoke("mlbKeys.register");

        await panel.Instance.DisposeAsync();

        JSInterop.VerifyInvoke("mlbKeys.unregister");
    }

    // ── The cards page ───────────────────────────────────────────────────────

    [Fact]
    public void TheCardsPage_ShowsAnOffenseAndADefenseCardWithTheRealKeys()
    {
        var page = Render<ShortcutCards>();

        var offense = page.Find("[data-testid=card-offense]");
        var defense = page.Find("[data-testid=card-defense]");

        Assert.Contains("You are batting", offense.TextContent);
        Assert.Contains("Steal", offense.TextContent);
        Assert.Equal("4", offense.QuerySelectorAll("tr").First(r => r.TextContent.Contains("Steal")).QuerySelector("kbd")!.TextContent);
        Assert.Contains("Play the pitch", offense.TextContent);
        Assert.Contains("Shift", offense.TextContent);   // the two-player rule

        Assert.Contains("You are fielding", defense.TextContent);
        foreach (var label in new[] { "Fastball", "Curveball", "Slider", "Changeup", "Infield in", "Corners in", "Guard the lines", "Intentional walk" })
            Assert.Contains(label, defense.TextContent);
        Assert.Equal("9", defense.QuerySelectorAll("tr").First(r => r.TextContent.Contains("Intentional")).QuerySelector("kbd")!.TextContent);
    }

    [Fact]
    public void EveryRowOnTheCardsIsABindingThatExists()
    {
        var cards = new[] { Shortcuts.OffenseCard(), Shortcuts.DefenseCard() };

        foreach (var card in cards)
            foreach (var section in card.Sections)
            {
                Assert.NotEmpty(section.Rows);
                foreach (var row in section.Rows.Where(r => r.Keys.Length == 1 && char.IsDigit(r.Keys[0])))
                {
                    var action = Shortcuts.Resolve($"Digit{row.Keys}", shift: false,
                        offense: card.Title == "OFFENSE", defense: card.Title == "DEFENSE");
                    Assert.NotEqual(ShortcutKind.None, action.Kind);
                }
            }
    }

    [Fact]
    public void TheCardsCanBePrinted()
    {
        JSInterop.SetupVoid("mlbExport.print").SetVoidResult();
        var page = Render<ShortcutCards>();

        page.FindAll("button").First(b => b.TextContent.Contains("Print cards")).Click();

        JSInterop.VerifyInvoke("mlbExport.print");
    }

    // ── The menu ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheMenuHasAKeyboardCardsItem()
    {
        Render<MudBlazor.MudPopoverProvider>();
        var layout = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "page"))));

        var link = layout.FindAll("a").First(a => a.TextContent.Contains("Keyboard Cards"));

        Assert.Equal("shortcuts", link.GetAttribute("href"));
    }
}
