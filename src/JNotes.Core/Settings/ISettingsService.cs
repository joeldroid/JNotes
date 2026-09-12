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

    Task<string> GetThemeAsync(CancellationToken cancellationToken = default);

    Task SetThemeAsync(string theme, CancellationToken cancellationToken = default);
}
