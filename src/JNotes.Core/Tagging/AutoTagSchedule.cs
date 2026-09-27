namespace JNotes.Core.Tagging;

/// <summary>
/// The rules for the background tagger: how long a note must have been left alone before it may be
/// written behind the user's back, and how a note's failures back off. A plain static class like
/// <c>ImageCleanupSchedule</c>, so the semantics are unit-testable without a host, a timer, or a database.
/// </summary>
public static class AutoTagSchedule
{
    /// <summary>
    /// A note changed more recently than this is left alone: an open editor autosaves its whole in-memory
    /// note, so a note must be quiet before it is written behind the user's back.
    /// </summary>
    public static readonly TimeSpan MinNoteAge = TimeSpan.FromMinutes(10);

    /// <summary>The failure count at which a note is given up on (<see cref="AutoTagOutcome.GaveUp"/>).</summary>
    public const int MaxFailures = 5;

    /// <summary>How long to wait after the <paramref name="failureCount"/>th failure: 1 h, 6 h, 24 h, then 72 h.</summary>
    public static TimeSpan RetryDelay(int failureCount) => failureCount switch
    {
        <= 1 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromHours(6),
        3 => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(72),
    };

    /// <summary>
    /// The outcome to record after a note's <paramref name="newFailureCount"/>th failure, and when it may
    /// next be tried (<c>null</c> once given up).
    /// </summary>
    public static (AutoTagOutcome Outcome, DateTimeOffset? RetryAfterUtc) AfterFailure(int newFailureCount, DateTimeOffset nowUtc) =>
        newFailureCount >= MaxFailures
            ? (AutoTagOutcome.GaveUp, null)
            : (AutoTagOutcome.Failed, nowUtc + RetryDelay(newFailureCount));
}
