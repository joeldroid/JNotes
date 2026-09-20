using MudBlazor;

namespace JNotes.Web.Theme;

/// <summary>
/// The one <see cref="MudTheme"/> the app uses. Shared by <c>MainLayout</c> (interactive) and
/// <c>AuthLayout</c> (static SSR, where <c>MudThemeProvider</c> still emits the palette CSS variables
/// server-side even though nothing on the page is interactive).
/// </summary>
public static class JNotesTheme
{
    // Roboto is vendored (wwwroot/lib/roboto, see fonts.css); the rest is the fallback chain.
    private static readonly string[] SansSerif =
        ["Roboto", "system-ui", "-apple-system", "Segoe UI", "Helvetica", "Arial", "sans-serif"];

    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2f6feb",
            AppbarBackground = "#1f2430",
            AppbarText = "#ffffff",
            DrawerBackground = "#f6f7f9",
            Background = "#ffffff",
            Surface = "#ffffff",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#6ea8fe",
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
