using JNotes.Core.Data;
using JNotes.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Settings;

/// <inheritdoc cref="ISettingsService"/>
public sealed class SettingsService : ISettingsService
{
    private const string NotesFolderPathKey = "NotesFolderPath";
    private const string ThemeKey = "Theme";
    private const string PasswordAuthEnabledKey = "PasswordAuthEnabled";

    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;
    private readonly JNotesOptions _options;
    private readonly OidcOptions _oidcOptions;

    public SettingsService(IDbContextFactory<JNotesDbContext> dbContextFactory, IOptions<JNotesOptions> options, IOptions<OidcOptions> oidcOptions)
    {
        _dbContextFactory = dbContextFactory;
        _options = options.Value;
        _oidcOptions = oidcOptions.Value;
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

    public async Task<bool> GetPasswordAuthEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([PasswordAuthEnabledKey], cancellationToken);
        return entry is null || bool.Parse(entry.Value);
    }

    public async Task SetPasswordAuthEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled && !_oidcOptions.IsConfigured)
        {
            throw new InvalidOperationException(
                "Can't disable password login while OIDC isn't configured — that would lock everyone out.");
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([PasswordAuthEnabledKey], cancellationToken);
        var value = enabled.ToString();
        if (entry is null)
        {
            db.Settings.Add(new SettingEntry { Key = PasswordAuthEnabledKey, Value = value });
        }
        else
        {
            entry.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
