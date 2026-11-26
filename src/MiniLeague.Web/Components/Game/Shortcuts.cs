using MiniLeague.Engine.Models;

namespace MiniLeague.Web.Components.Game;

public enum ShortcutKind { None, Offense, Pitch, Alignment, IntentionalWalk, Play, Substitutions, BoxScore, Hint }

public readonly record struct ShortcutAction(
    ShortcutKind Kind,
    OffensiveStrategy Strategy = default,
    PitchType Pitch = default,
    DefensiveAlignment Alignment = default);

public record ShortcutRow(string Keys, string Action);
public record ShortcutSection(string Heading, IReadOnlyList<ShortcutRow> Rows);
public record ShortcutCard(string Title, string Subtitle, IReadOnlyList<ShortcutSection> Sections, string Footer);

/// <summary>
/// The keyboard shortcuts for managing a game. One table drives both the live keys in the decision
/// panel and the printable cards, so the cards always describe what the keys really do.
/// </summary>
public static class Shortcuts
{
    // Number keys, as in the original game's number-key strategy cards.
    public static readonly (int Key, OffensiveStrategy Strategy)[] OffenseKeys =
    [
        (1, OffensiveStrategy.SwingAway),
        (2, OffensiveStrategy.SwingAgressive),
        (3, OffensiveStrategy.SwingSafe),
        (4, OffensiveStrategy.Steal),
        (5, OffensiveStrategy.HitAndRun),
        (6, OffensiveStrategy.SacrificeBunt),
        (7, OffensiveStrategy.Squeeze),
        (8, OffensiveStrategy.SafetySqueeze),
    ];

    public static readonly (int Key, PitchType Pitch)[] PitchKeys =
    [
        (1, PitchType.Fastball), (2, PitchType.Curveball), (3, PitchType.Slider), (4, PitchType.Changeup),
    ];

    public static readonly (int Key, DefensiveAlignment Alignment)[] AlignmentKeys =
    [
        (5, DefensiveAlignment.Standard), (6, DefensiveAlignment.InfieldIn),
        (7, DefensiveAlignment.CornersIn), (8, DefensiveAlignment.GuardLines),
        (0, DefensiveAlignment.ShiftPull),
    ];

    // WHY: digits 1-9 are all taken, so the second shift sits next to 0 on the number row. It is not a digit,
    // so it is matched by its KeyboardEvent.code instead of through AlignmentKeys.
    public const string ShiftOppositeKeyLabel = "-";
    private static bool IsShiftOppositeCode(string code) => code is "Minus" or "NumpadSubtract";

    public const int IntentionalWalkKey = 9;

    // ── Labels ───────────────────────────────────────────────────────────────

    public static string Label(OffensiveStrategy s) => s switch
    {
        OffensiveStrategy.SwingAway      => "Swing Away",
        OffensiveStrategy.SwingAgressive => "Aggressive",
        OffensiveStrategy.SwingSafe      => "Safe",
        OffensiveStrategy.Steal          => "Steal",
        OffensiveStrategy.HitAndRun      => "Hit & Run",
        OffensiveStrategy.SacrificeBunt  => "Sacrifice",
        OffensiveStrategy.Squeeze        => "Squeeze",
        OffensiveStrategy.SafetySqueeze  => "Safe squeeze",
        _ => s.ToString()
    };

    public static string Label(DefensiveAlignment a) => a switch
    {
        DefensiveAlignment.Standard   => "Normal",
        DefensiveAlignment.InfieldIn  => "Infield in",
        DefensiveAlignment.CornersIn  => "Corners in",
        DefensiveAlignment.GuardLines => "Guard the lines",
        DefensiveAlignment.ShiftPull  => "Shift to the pull side",
        DefensiveAlignment.ShiftOpposite => "Shift to the opposite field",
        _ => a.ToString()
    };

    public static string KeyFor(OffensiveStrategy s) =>
        OffenseKeys.Where(k => k.Strategy == s).Select(k => k.Key.ToString()).FirstOrDefault() ?? "";

    public static string KeyFor(PitchType p) =>
        PitchKeys.Where(k => k.Pitch == p).Select(k => k.Key.ToString()).FirstOrDefault() ?? "";

    public static string KeyFor(DefensiveAlignment a) =>
        a == DefensiveAlignment.ShiftOpposite ? ShiftOppositeKeyLabel :
        AlignmentKeys.Where(k => k.Alignment == a).Select(k => k.Key.ToString()).FirstOrDefault() ?? "";

    // ── Reading a key press ──────────────────────────────────────────────────

    /// <summary>
    /// What a key press means. With one side to manage, plain number keys apply to it; when one person
    /// manages both teams, plain numbers are the offense and Shift + number is the defense.
    /// </summary>
    /// <param name="code">The browser's KeyboardEvent.code, e.g. "Digit4", "Numpad4", "Enter", "KeyS".</param>
    public static ShortcutAction Resolve(string code, bool shift, bool offense, bool defense, bool showHints = true)
    {
        if (!offense && !defense) return default;

        switch (code)
        {
            case "Enter" or "NumpadEnter" or "KeyP": return new(ShortcutKind.Play);
            case "KeyS": return new(ShortcutKind.Substitutions);
            case "KeyB": return new(ShortcutKind.BoxScore);
            case "KeyH": return showHints ? new(ShortcutKind.Hint) : default;
        }

        if (IsShiftOppositeCode(code))
        {
            // Defense only; with both teams managed it needs Shift like every other defense key.
            bool defenseKey = offense && defense ? shift : defense;
            return defenseKey ? new(ShortcutKind.Alignment, Alignment: DefensiveAlignment.ShiftOpposite) : default;
        }

        int? digit = code switch
        {
            ['D', 'i', 'g', 'i', 't', var d] when char.IsDigit(d) => d - '0',
            ['N', 'u', 'm', 'p', 'a', 'd', var d] when char.IsDigit(d) => d - '0',
            _ => null
        };
        if (digit is not int key) return default;

        bool useDefense = offense && defense ? shift : defense;
        if (!useDefense)
        {
            foreach (var (k, strategy) in OffenseKeys)
                if (k == key) return new(ShortcutKind.Offense, Strategy: strategy);
            return default;
        }

        foreach (var (k, pitch) in PitchKeys)
            if (k == key) return new(ShortcutKind.Pitch, Pitch: pitch);
        foreach (var (k, alignment) in AlignmentKeys)
            if (k == key) return new(ShortcutKind.Alignment, Alignment: alignment);
        return key == IntentionalWalkKey ? new(ShortcutKind.IntentionalWalk) : default;
    }

    // ── The printable cards ──────────────────────────────────────────────────

    private static ShortcutSection Game() => new("During play", [
        new("Enter  or  P", "Play the pitch"),
        new("S", "Substitutions, bullpen and bench"),
        new("B", "Box score"),
        new("H", "Show hint"),
    ]);

    public static ShortcutCard OffenseCard() => new(
        "OFFENSE", "You are batting",
        [
            new("Strategy", OffenseKeys.Select(k => new ShortcutRow(k.Key.ToString(), Label(k.Strategy))).ToList()),
            Game(),
        ],
        "Managing both teams? These keys are the offense as shown. The defense uses Shift + number.");

    public static ShortcutCard DefenseCard() => new(
        "DEFENSE", "You are fielding",
        [
            new("Pitch", PitchKeys.Select(k => new ShortcutRow(k.Key.ToString(), k.Pitch.ToString())).ToList()),
            new("Alignment", AlignmentKeys.Select(k => new ShortcutRow(k.Key.ToString(), Label(k.Alignment)))
                .Append(new ShortcutRow(ShiftOppositeKeyLabel, Label(DefensiveAlignment.ShiftOpposite))).ToList()),
            new("Special", [new ShortcutRow(IntentionalWalkKey.ToString(), "Intentional walk")]),
            Game(),
        ],
        "Managing both teams? Hold Shift with the number for these. Plain numbers are the offense.");
}
