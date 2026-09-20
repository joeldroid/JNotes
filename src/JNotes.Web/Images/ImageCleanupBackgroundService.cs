using JNotes.Core.Images;
using JNotes.Core.Settings;

namespace JNotes.Web.Images;

/// <summary>
/// Deletes every user's unused images on the admin-set schedule (Settings → Administration → Images:
/// every N days, 0 = off, default 7). Unlike <c>AutoIndexBackgroundService</c>, whose interval is an env
/// var read once at startup, this interval is an in-app setting an admin can change at runtime — so it
/// polls, re-reading the setting each time, rather than sleeping for the whole interval. The last run is
/// persisted (see <c>ISettingsService.GetImageCleanupLastRunUtcAsync</c>) so a restart neither skips a
/// pass nor, for a container that restarts more often than the interval, postpones it forever. Lives in
/// JNotes.Web for the same reason as the auto-index service (<c>BackgroundService</c> is a hosting-layer
/// type); the work is Core's <c>IImageCleanupService</c>.
/// </summary>
public sealed class ImageCleanupBackgroundService : BackgroundService
{
    // Cheap (one small SQLite read) and short enough that enabling the setting or shortening the interval
    // takes effect promptly, long enough to be irrelevant against an interval measured in days.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageCleanupBackgroundService> _logger;

    public ImageCleanupBackgroundService(IServiceScopeFactory scopeFactory, ILogger<ImageCleanupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            // Checks once right away (startup) and then on every tick.
            do
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never rethrow: the default BackgroundServiceExceptionBehavior.StopHost would take the
                    // whole app down over a housekeeping pass. It retries on the next poll.
                    _logger.LogError(ex, "Automatic image cleanup check failed; will retry on the next poll.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var intervalDays = await settings.GetImageCleanupIntervalDaysAsync(cancellationToken);
        if (!ImageCleanupSchedule.IsEnabled(intervalDays))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var lastRun = await settings.GetImageCleanupLastRunUtcAsync(cancellationToken);
        if (lastRun is null)
        {
            // First time this has been enabled (a fresh install, or an upgrade to a version with the
            // feature): start the clock now instead of deleting on first startup, so an admin has a
            // full interval to notice and turn it off.
            await settings.SetImageCleanupLastRunUtcAsync(now, cancellationToken);
            _logger.LogInformation("Automatic image cleanup is on: first pass in {IntervalDays} day(s).", intervalDays);
            return;
        }

        if (!ImageCleanupSchedule.IsDue(intervalDays, lastRun, now))
        {
            return;
        }

        var cleanup = scope.ServiceProvider.GetRequiredService<IImageCleanupService>();
        var result = await cleanup.CleanUpAllUsersAsync(ImageCleanupSchedule.MinImageAge, cancellationToken);

        _logger.LogInformation(
            "Image cleanup: removed {Deleted} unused image(s) ({BytesFreed} bytes) across {UsersCleaned} user(s); {UsersFailed} failed.",
            result.Deleted,
            result.BytesFreed,
            result.UsersCleaned,
            result.UsersFailed);
    }
}
