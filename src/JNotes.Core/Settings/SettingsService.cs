using System.Globalization;
using JNotes.Core.Data;
using JNotes.Core.Images;
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
    private const string ImageCleanupIntervalDaysKey = "ImageCleanupIntervalDays";
    private const string ImageCleanupLastRunKey = "ImageCleanupLastRunUtc";

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
        // Dark is the app-wide default — a user who has never touched the toggle gets dark until
        // they explicitly pick light, not the other way around.
        return entry?.Value ?? AppTheme.Dark;
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

    public async Task<int> GetImageCleanupIntervalDaysAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([ImageCleanupIntervalDaysKey], cancellationToken);
        // TryParse + range check so a hand-edited value falls back to the default instead of throwing
        // on every poll of the background service.
        return entry is not null
            && int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            && ImageCleanupSchedule.IsValidIntervalDays(days)
                ? days
                : ImageCleanupSchedule.DefaultIntervalDays;
    }

    public async Task SetImageCleanupIntervalDaysAsync(int days, CancellationToken cancellationToken = default)
    {
        if (!ImageCleanupSchedule.IsValidIntervalDays(days))
        {
            throw new ArgumentOutOfRangeException(
                nameof(days), days, $"Must be between {ImageCleanupSchedule.DisabledIntervalDays} and {ImageCleanupSchedule.MaxIntervalDays} days.");
        }

        await SetAppSettingAsync(ImageCleanupIntervalDaysKey, days.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task<DateTimeOffset?> GetImageCleanupLastRunUtcAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.Settings.FindAsync([ImageCleanupLastRunKey], cancellationToken);
        return entry is not null
            && DateTimeOffset.TryParse(entry.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                ? when
                : null;
    }

    public Task SetImageCleanupLastRunUtcAsync(DateTimeOffset whenUtc, CancellationToken cancellationToken = default) =>
        SetAppSettingAsync(ImageCleanupLastRunKey, whenUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), cancellationToken);

    private async Task SetAppSettingAsync(string key, string value, CancellationToken cancellationToken)
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
