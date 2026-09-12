using JNotes.Core.Settings;

namespace JNotes.Tests.TestDoubles;

/// <summary>In-memory <see cref="ISettingsService"/> for tests that don't need the real EF Core-backed one.</summary>
internal sealed class StubSettingsService(string notesFolderPath, string theme = "light") : ISettingsService
{
    private string _notesFolderPath = notesFolderPath;
    private string _theme = theme;

    public Task<string> GetNotesFolderPathAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_notesFolderPath);

    public Task SetNotesFolderPathAsync(string path, CancellationToken cancellationToken = default)
    {
        _notesFolderPath = path;
        return Task.CompletedTask;
    }

    public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => Task.FromResult(_theme);

    public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default)
    {
        _theme = theme;
        return Task.CompletedTask;
    }
}
