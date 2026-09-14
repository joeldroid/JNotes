using JNotes.Core.Settings;

namespace JNotes.Tests.TestDoubles;

/// <summary>In-memory <see cref="ISettingsService"/> for tests that don't need the real EF Core-backed one.</summary>
internal sealed class StubSettingsService(string notesFolderPath) : ISettingsService
{
    private string _notesFolderPath = notesFolderPath;
    private readonly Dictionary<Guid, string> _themesByUser = [];

    public Task<string> GetNotesFolderPathAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_notesFolderPath);

    public Task SetNotesFolderPathAsync(string path, CancellationToken cancellationToken = default)
    {
        _notesFolderPath = path;
        return Task.CompletedTask;
    }

    public Task<string> GetThemeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_themesByUser.GetValueOrDefault(userId, AppTheme.Light));

    public Task SetThemeAsync(Guid userId, string theme, CancellationToken cancellationToken = default)
    {
        _themesByUser[userId] = theme;
        return Task.CompletedTask;
    }
}
