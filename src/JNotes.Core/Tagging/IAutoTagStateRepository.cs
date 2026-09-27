using JNotes.Core.Data;

namespace JNotes.Core.Tagging;

/// <summary>
/// The background tagger's persisted memory (<c>NoteAutoTagAttempts</c>, see <see cref="NoteAutoTagAttempt"/>)
/// and its candidate query. Explicit <c>userId</c> throughout, like <c>INoteIndexRepository</c>: the caller is
/// a background service with no current user. All <see cref="DateTimeOffset"/> arguments must be UTC.
/// </summary>
public interface IAutoTagStateRepository
{
    /// <summary>
    /// Up to <paramref name="limit"/> of <paramref name="userId"/>'s live notes worth tagging, newest first: no
    /// tags, some title or body, indexed <c>UpdatedAt</c> before <paramref name="updatedBeforeUtc"/>, and no
    /// attempt row — except a <see cref="AutoTagOutcome.Failed"/> one whose retry time has passed at
    /// <paramref name="nowUtc"/>. Only a *pick list*: the file is re-checked before anything is sent or written.
    /// </summary>
    /// <remarks>
    /// Reads the <c>NoteIndex*</c> tables (via the shared context) rather than going through
    /// <c>INoteIndexRepository</c>, so "untagged and not yet attempted" is one efficient <c>NOT EXISTS</c> query
    /// instead of a per-note round trip.
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetCandidateNoteIdsAsync(
        Guid userId, DateTimeOffset updatedBeforeUtc, DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>Records a final <paramref name="outcome"/> (upsert, clearing the retry time, keeping any failure count).</summary>
    Task RecordOutcomeAsync(Guid userId, Guid noteId, AutoTagOutcome outcome, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts one more failure and sets <see cref="AutoTagOutcome.Failed"/> with a backoff, or
    /// <see cref="AutoTagOutcome.GaveUp"/> once <see cref="AutoTagSchedule.MaxFailures"/> is reached. A single
    /// hosted service is the only writer, so read-modify-write is enough.
    /// </summary>
    Task RecordFailureAsync(Guid userId, Guid noteId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>The attempt row for a note, or <c>null</c> if it has never been attempted.</summary>
    Task<NoteAutoTagAttempt?> GetAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);
}
