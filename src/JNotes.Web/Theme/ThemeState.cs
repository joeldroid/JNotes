namespace JNotes.Web.Theme;

/// <summary>
/// The signed-in user's light/dark choice for the current circuit. <c>MainLayout</c> seeds it from
/// <c>ISettingsService</c> and re-renders <c>MudThemeProvider</c> when it changes; <c>Settings.razor</c>
/// flips it so the switch applies live without a reload.
/// </summary>
public sealed class ThemeState
{
    public bool IsDark { get; private set; } = true;

    /// <summary>Raised by <see cref="Set"/> only — <see cref="Initialize"/> is silent because it runs during the layout's own first render.</summary>
    public event Action? Changed;

    public void Initialize(bool isDark) => IsDark = isDark;

    public void Set(bool isDark)
    {
        if (IsDark == isDark)
        {
            return;
        }

        IsDark = isDark;
        Changed?.Invoke();
    }
}
