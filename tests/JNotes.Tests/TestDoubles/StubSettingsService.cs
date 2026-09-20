using JNotes.Core.Images;
using JNotes.Core.Settings;

namespace JNotes.Tests.TestDoubles;

/// <summary>In-memory <see cref="ISettingsService"/> for tests that don't need the real EF Core-backed one.</summary>
internal sealed class StubSettingsService(string notesFolderPath) : ISettingsService
{
    private string _notesFolderPath = notesFolderPath;
    private readonly Dictionary<Guid, string> _themesByUser = [];
    private bool _passwordAuthEnabled = true;
    private int _imageCleanupIntervalDays = ImageCleanupSchedule.DefaultIntervalDays;
    private DateTimeOffset? _imageCleanupLastRunUtc;

    public Task<string> GetNotesFolderPathAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_notesFolderPath);

    public Task SetNotesFolderPathAsync(string path, CancellationToken cancellationToken = default)
    {
        _notesFolderPath = path;
        return Task.CompletedTask;
    }

    public Task<string> GetThemeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_themesByUser.GetValueOrDefault(userId, AppTheme.Dark));

    public Task SetThemeAsync(Guid userId, string theme, CancellationToken cancellationToken = default)
    {
        _themesByUser[userId] = theme;
        return Task.CompletedTask;
    }

    public Task<bool> GetPasswordAuthEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_passwordAuthEnabled);

    public Task SetPasswordAuthEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _passwordAuthEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<int> GetImageCleanupIntervalDaysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_imageCleanupIntervalDays);

    public Task SetImageCleanupIntervalDaysAsync(int days, CancellationToken cancellationToken = default)
    {
        _imageCleanupIntervalDays = days;
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetImageCleanupLastRunUtcAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_imageCleanupLastRunUtc);

    public Task SetImageCleanupLastRunUtcAsync(DateTimeOffset whenUtc, CancellationToken cancellationToken = default)
    {
        _imageCleanupLastRunUtc = whenUtc;
        return Task.CompletedTask;
    }
}
