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

    public async Task<string> GetNotesFolderPathAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([NotesFolderPathKey], cancellationToken);
        return entry?.Value ?? _options.NotesFolderPath;
    }

    public async Task SetNotesFolderPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([NotesFolderPathKey], cancellationToken);
        if (entry is null)
        {
            db.Settings.Add(new SettingEntry { Key = NotesFolderPathKey, Value = path });
        }
        else
        {
            entry.Value = path;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> GetThemeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.UserSettings.FindAsync([userId, ThemeKey], cancellationToken);
        return entry?.Value ?? AppTheme.Light;
    }

    public async Task SetThemeAsync(Guid userId, string theme, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.UserSettings.FindAsync([userId, ThemeKey], cancellationToken);
        if (entry is null)
        {
            db.UserSettings.Add(new UserSetting { UserId = userId, Key = ThemeKey, Value = theme });
        }
        else
        {
            entry.Value = theme;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
