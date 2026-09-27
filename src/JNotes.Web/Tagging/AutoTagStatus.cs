using JNotes.Core.Tagging;

namespace JNotes.Web.Tagging;

/// <summary>What the Settings status card shows; an immutable copy, so a render never sees a half-updated state.</summary>
public sealed record AutoTagStatusSnapshot(
    bool IsRunning,
    DateTimeOffset? LastPassFinishedUtc,
    int LastPassTagged,
    int TaggedSinceStartup,
    string? UnavailableReason,
    DateTimeOffset? UnavailableSinceUtc);

/// <summary>
/// In-memory state of the background tagger, written by <see cref="AutoTagBackgroundService"/> and read by the
/// Settings → Administration "Auto-tagging" card. A singleton, thread-safe (one lock), and deliberately not
/// persisted: it resets on restart and the card doesn't update live.
/// </summary>
public sealed class AutoTagStatus
{
    private readonly object _gate = new();
    private AutoTagStatusSnapshot _snapshot = new(false, null, 0, 0, null, null);

    public AutoTagStatusSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void PassStarted()
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { IsRunning = true };
        }
    }

    /// <summary>
    /// Records the end of a pass; <paramref name="result"/> is <c>null</c> when the pass threw or was cancelled
    /// (only the running flag is cleared then). Unavailability is set by a pass that stopped early, keeping the
    /// time of the *first* such pass, and cleared only by a pass that actually reached the model (a pass with
    /// nothing to send proves nothing about Ollama). Returns whether Ollama was unavailable *before* this pass,
    /// so the caller can log the transition once.
    /// </summary>
    public bool PassFinished(AutoTagPassResult? result, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var wasUnavailable = _snapshot.UnavailableReason is not null;

            if (result is null)
            {
                _snapshot = _snapshot with { IsRunning = false };
                return wasUnavailable;
            }

            var reachedModel = result.Tagged + result.NoTags + result.Failed > 0;
            var reason = _snapshot.UnavailableReason;
            var since = _snapshot.UnavailableSinceUtc;
            if (result.ServiceUnavailable)
            {
                reason = result.UnavailableReason;
                since = wasUnavailable ? since : nowUtc;
            }
            else if (reachedModel)
            {
                reason = null;
                since = null;
            }

            _snapshot = _snapshot with
            {
                IsRunning = false,
                LastPassFinishedUtc = nowUtc,
                LastPassTagged = result.Tagged,
                TaggedSinceStartup = _snapshot.TaggedSinceStartup + result.Tagged,
                UnavailableReason = reason,
                UnavailableSinceUtc = since,
            };

            return wasUnavailable;
        }
    }
}
