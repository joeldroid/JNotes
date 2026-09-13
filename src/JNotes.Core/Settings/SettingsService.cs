using JNotes.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Settings;

/// <inheritdoc cref="ISettingsService"/>
public sealed class SettingsService : ISettingsService
{
    private const string NotesFolderPathKey = "NotesFolderPath";
    private const string ThemeKey = "Theme";

    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;
    private readonly JNotesOptions _options;

    public SettingsService(IDbContextFactory<JNotesDbContext> dbContextFactory, IOptions<JNotesOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _options = options.Value;
    }

    public Task<string> GetNotesFolderPathAsync(CancellationToken cancellationToken = default) =>
        GetAsync(NotesFolderPathKey, _options.NotesFolderPath, cancellationToken);

    public Task SetNotesFolderPathAsync(string path, CancellationToken cancellationToken = default) =>
        SetAsync(NotesFolderPathKey, path, cancellationToken);

    public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) =>
        GetAsync(ThemeKey, AppTheme.Light, cancellationToken);

    public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) =>
        SetAsync(ThemeKey, theme, cancellationToken);

    private async Task<string> GetAsync(string key, string defaultValue, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([key], cancellationToken);
        return entry?.Value ?? defaultValue;
    }

    private async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([key], cancellationToken);
        if (entry is null)
        {
            db.Settings.Add(new SettingEntry { Key = key, Value = value });
        }
        else
        {
            entry.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
