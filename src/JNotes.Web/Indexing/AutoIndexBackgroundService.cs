using JNotes.Core;
using JNotes.Core.Indexing;
using Microsoft.Extensions.Options;

namespace JNotes.Web.Indexing;

/// <summary>
/// Periodically reconciles every user's Redis index against their note files, catching up
/// ContentOnly autosaves and hand-edited files without waiting for a restart or a manual
/// "Rescan Now". Interval comes from AUTO_INDEX_INTERVAL_MINUTES (0 disables). Lives in
/// JNotes.Web rather than Core because BackgroundService is a hosting-layer type Core has never
/// depended on — the same split as ICurrentUserContext; all the actual work is Core's
/// IAllUsersIndexReconciler.
/// </summary>
public sealed class AutoIndexBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JNotesOptions _options;
    private readonly ILogger<AutoIndexBackgroundService> _logger;

    public AutoIndexBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<JNotesOptions> options,
        ILogger<AutoIndexBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _options.AutoIndexIntervalMinutes;
        if (!AutoIndexInterval.IsEnabled(minutes))
        {
            _logger.LogInformation("Automatic index reconciliation is disabled (AUTO_INDEX_INTERVAL_MINUTES=0).");
            return;
        }

        _logger.LogInformation("Automatic index reconciliation enabled: every {IntervalMinutes} minute(s).", minutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));

        try
        {
            // Waiting before the first tick is deliberate — RunStartupReconciliationAsync just ran
            // as part of startup, so an immediate tick here would be redundant work. Returning false
            // / throwing OperationCanceledException from WaitForNextTickAsync is the only exit — no
            // Task.Delay loop, no polling stoppingToken.IsCancellationRequested.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var reconciler = scope.ServiceProvider.GetRequiredService<IAllUsersIndexReconciler>();
                    var result = await reconciler.ReconcileAllAsync(stoppingToken);

                    // Avoid a log line every interval forever — only worth Information when
                    // something actually happened.
                    var changed = result.Added + result.Updated + result.Removed;
                    if (changed > 0 || result.UsersFailed > 0)
                    {
                        _logger.LogInformation(
                            "Auto-index: {Added} added, {Updated} updated, {Removed} removed across {UsersReconciled} user(s); {UsersFailed} failed.",
                            result.Added,
                            result.Updated,
                            result.Removed,
                            result.UsersReconciled,
                            result.UsersFailed);
                    }
                    else
                    {
                        _logger.LogDebug("Auto-index: no changes across {UsersReconciled} user(s).", result.UsersReconciled);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never rethrow: the default BackgroundServiceExceptionBehavior.StopHost would
                    // otherwise take the whole app down because the index (a derived cache) hiccuped.
                    _logger.LogError(ex, "Automatic index reconciliation pass failed; will retry on the next interval.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
