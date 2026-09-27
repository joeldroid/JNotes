using MudBlazor;

namespace JNotes.Web.Theme;

/// <summary>
/// The one <see cref="MudTheme"/> the app uses. The app is dark-only (<c>IsDarkMode="true"</c> in both
/// <c>MainLayout</c> and <c>AuthLayout</c>), so only <see cref="MudTheme.PaletteDark"/> is set; the
/// default <see cref="MudTheme.PaletteLight"/> is never emitted.
/// </summary>
public static class JNotesTheme
{
    // Roboto is vendored (wwwroot/lib/roboto, see fonts.css); the rest is the fallback chain.
    private static readonly string[] SansSerif =
        ["Roboto", "system-ui", "-apple-system", "Segoe UI", "Helvetica", "Arial", "sans-serif"];

    public static MudTheme Instance { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#4fd1c5",
            PrimaryContrastText = "#0b1f1d",
            Secondary = "#4fd1c5",
            AppbarBackground = "#1a1d23",
            DrawerBackground = "#1a1d23",
            Background = "#14161a",
            Surface = "#1e2128",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            DrawerWidthLeft = "240px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = SansSerif },
        },
    };
}
