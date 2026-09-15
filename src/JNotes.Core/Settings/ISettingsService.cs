namespace JNotes.Core.Settings;

/// <summary>
/// Typed access to app settings (backed by the <c>Settings</c> table — see <c>SettingsService</c>).
/// Callers never see raw setting keys.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// The folder note markdown files are read from/written to. Changing this setting takes
    /// effect on the next app restart — <c>FileSystemNoteFileStore</c> re-resolves it per call
    /// rather than caching it, but nothing currently reconciles an in-flight folder switch.
    /// </summary>
    Task<string> GetNotesFolderPathAsync(CancellationToken cancellationToken = default);

    Task SetNotesFolderPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Per-user preference (backed by the separate <c>UserSettings</c> table), unlike the installation-wide settings above.</summary>
    Task<string> GetThemeAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SetThemeAsync(Guid userId, string theme, CancellationToken cancellationToken = default);

    /// <summary>Whether the username/password login form is shown at all. Defaults to true.</summary>
    Task<bool> GetPasswordAuthEnabledAsync(CancellationToken cancellationToken = default);

    /// <exception cref="InvalidOperationException">
    /// Disabling password auth (<paramref name="enabled"/> is false) while OIDC isn't configured
    /// would lock every user out — refused rather than silently accepted.
    /// </exception>
    Task SetPasswordAuthEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
