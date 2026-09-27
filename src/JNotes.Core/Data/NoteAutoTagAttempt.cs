using JNotes.Core.Tagging;

namespace JNotes.Core.Data;

/// <summary>
/// The background tagger's memory: one row per (user, note) it has attempted, so a note gets one final
/// attempt and failures back off instead of being retried every pass. Unlike the <c>NoteIndex*</c> tables
/// this is NOT derived — it can't be rebuilt from the markdown files, which is why it deliberately has no
/// <c>NoteIndex</c> prefix. Truncating it is safe but not free: every untagged note (including ones whose
/// tags a user removed after an auto-tag) gets one more attempt. Cascades with the user; deliberately no FK
/// to <c>NoteIndexEntries</c>, because the index is truncatable. Owned by <c>PostgresAutoTagStateRepository</c>.
/// </summary>
public sealed class NoteAutoTagAttempt
{
    public required Guid UserId { get; set; }

    public required Guid NoteId { get; set; }

    /// <summary>Stored as the enum's name (see <see cref="AutoTagOutcome"/>), never its ordinal.</summary>
    public required AutoTagOutcome Outcome { get; set; }

    /// <summary>Failed attempts so far; kept when a later attempt ends in a final outcome.</summary>
    public int FailureCount { get; set; }

    public required DateTimeOffset LastAttemptUtc { get; set; }

    /// <summary>When a <see cref="AutoTagOutcome.Failed"/> note may be tried again; <c>null</c> for final outcomes.</summary>
    public DateTimeOffset? RetryAfterUtc { get; set; }
}
