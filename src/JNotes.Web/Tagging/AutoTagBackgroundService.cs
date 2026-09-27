using JNotes.Core.Tagging;
using Microsoft.Extensions.Options;

namespace JNotes.Web.Tagging;

/// <summary>
/// Tags untagged notes with a local Ollama model, one pass every <c>AUTO_TAG_INTERVAL_MINUTES</c>, and only when
/// <c>OLLAMA_URL</c> is set (<see cref="AutoTagOptions.IsEnabled"/>; there is no separate flag) — otherwise it
/// returns immediately, and <c>Program.cs</c> has already logged the state. Like
/// <c>ImageCleanupBackgroundService</c> it checks once shortly after startup and then on every tick, making a
/// scope per pass and never rethrowing (the default behaviour would take the whole host down over a housekeeping
/// job). Lives in JNotes.Web for the same reason as the other two (<c>BackgroundService</c> is a hosting-layer
/// type); the work is Core's <c>INoteAutoTagger</c>. Logs state transitions only — never note content, tags or
/// the Ollama URL.
/// </summary>
public sealed class AutoTagBackgroundService : BackgroundService
{
    // Lets the app finish starting (and Ollama, if it's a sibling container) before the first request.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AutoTagOptions _options;
    private readonly AutoTagStatus _status;
    private readonly ILogger<AutoTagBackgroundService> _logger;

    public AutoTagBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<AutoTagOptions> options,
        AutoTagStatus status,
        ILogger<AutoTagBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsEnabled)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));

            // A pass that outlasts the interval just delays the next one: PeriodicTimer never queues more than a tick.
            do
            {
                try
                {
                    await RunPassAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-tagging pass failed; will retry on the next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        _status.PassStarted();

        AutoTagPassResult? result = null;
        bool wasUnavailable;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var tagger = scope.ServiceProvider.GetRequiredService<INoteAutoTagger>();
            result = await tagger.TagAllUsersAsync(_options.BatchSize, AutoTagSchedule.MinNoteAge, cancellationToken);
        }
        finally
        {
            // Also when the pass threw or was cancelled, so the status card never shows "running" forever.
            wasUnavailable = _status.PassFinished(result, DateTimeOffset.UtcNow);
        }

        if (result.ServiceUnavailable)
        {
            if (wasUnavailable)
            {
                _logger.LogDebug("Auto-tagging is still paused: {Reason}", result.UnavailableReason);
            }
            else
            {
                // The reason text is user-safe: no URL, no note content.
                _logger.LogWarning(
                    "Auto-tagging paused: {Reason} Retrying every {IntervalMinutes} minute(s).",
                    result.UnavailableReason,
                    _options.IntervalMinutes);
            }
        }
        else if (wasUnavailable && _status.Snapshot.UnavailableReason is null)
        {
            _logger.LogInformation("Auto-tagging resumed.");
        }

        if (result.Tagged + result.NoTags + result.Failed + result.Skipped > 0 || result.UsersFailed > 0)
        {
            _logger.LogInformation(
                "Auto-tag: {Tagged} tagged, {NoTags} without suggestions, {Skipped} skipped, {Failed} failed, {Deferred} deferred; {UsersFailed} user(s) failed.",
                result.Tagged,
                result.NoTags,
                result.Skipped,
                result.Failed,
                result.Deferred,
                result.UsersFailed);
        }
        else
        {
            _logger.LogDebug("Auto-tag: nothing to do ({Deferred} deferred).", result.Deferred);
        }
    }
}
