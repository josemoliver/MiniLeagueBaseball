using MudBlazor;

namespace MiniLeague.Web;

/// <summary>
/// The app's colors, taken from the startup artwork: its royal-blue sky, the red of its logo stripes and
/// ball stitching, and its cream lettering.
/// </summary>
public static class AppTheme
{
    /// <summary>Royal blue of the cover art; white text on it has a contrast ratio of about 8:1.</summary>
    public const string ArtworkBlue = "#0b4ea2";

    /// <summary>A deeper shade of the same blue for dark mode.</summary>
    public const string ArtworkBlueDark = "#0a3f85";

    /// <summary>The logo red (the art's is about #e04040), deepened a little so white text on it stays readable.</summary>
    public const string ArtworkRed = "#c8323a";

    /// <summary>The cream of the lettering, for page backgrounds.</summary>
    public const string ArtworkCream = "#f8f8f0";

    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = ArtworkBlue,
            PrimaryContrastText = "#ffffff",
            Secondary = ArtworkRed,
            SecondaryContrastText = "#ffffff",
            Info = ArtworkBlue,
            AppbarBackground = ArtworkBlue,
            AppbarText = "#ffffff",
            Background = "#f3f5fa",
            Surface = "#ffffff",
            DrawerBackground = ArtworkCream,
            DrawerText = "#1d2a44",
            TextPrimary = "#1d2a44",
            TextSecondary = "#55627d",
            ActionDefault = "#3b4a68",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#6aa6ff",
            PrimaryContrastText = "#06142b",
            Secondary = "#ff7a82",
            SecondaryContrastText = "#2a0a0d",
            AppbarBackground = ArtworkBlueDark,
            AppbarText = "#ffffff",
            Background = "#0f1724",
            Surface = "#172033",
            DrawerBackground = "#121b2c",
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" },
    };
}
