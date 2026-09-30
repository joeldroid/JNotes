namespace JNotes.Core.Sharing;

/// <summary>
/// The <c>NoteShares</c> grants (PostgreSQL — see <see cref="PostgresNoteShareRepository"/>). Every method
/// takes explicit ids, like <c>INoteIndexRepository</c>; <c>NoteService</c> and <c>NoteSharingService</c>
/// (implicit current user) are the only callers and do the authorisation, this is plain storage.
/// </summary>
public interface INoteShareRepository
{
    /// <summary>The grant giving <paramref name="sharedWithUserId"/> access to <paramref name="noteId"/>, or <c>null</c>.</summary>
    Task<NoteShareGrant?> FindAsync(Guid noteId, Guid sharedWithUserId, CancellationToken cancellationToken = default);

    /// <summary>Every recipient of <paramref name="ownerUserId"/>'s <paramref name="noteId"/>, ordered by display name.</summary>
    Task<IReadOnlyList<NoteShareEntry>> ListForNoteAsync(Guid ownerUserId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>Adds the grant or changes its permission. Never takes over another owner's grant for the same (note, recipient).</summary>
    Task UpsertAsync(Guid ownerUserId, Guid noteId, Guid sharedWithUserId, NoteSharePermission permission, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Removes one recipient's grant on the owner's note. <c>false</c> if there was none.</summary>
    Task<bool> DeleteAsync(Guid ownerUserId, Guid noteId, Guid sharedWithUserId, CancellationToken cancellationToken = default);

    /// <summary>Removes the recipient's own grant (Leave). <c>false</c> if there was none.</summary>
    Task<bool> DeleteForRecipientAsync(Guid noteId, Guid sharedWithUserId, CancellationToken cancellationToken = default);

    /// <summary>Removes every grant on the owner's note (used when the note is purged).</summary>
    Task DeleteAllForNoteAsync(Guid ownerUserId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every user except the owner and the note's existing recipients whose username or first/last name contains
    /// <paramref name="term"/> (all of them when blank), by username, at most <paramref name="limit"/> (clamped 1..20).
    /// Never matches on or returns an email address.
    /// </summary>
    Task<IReadOnlyList<ShareRecipientCandidate>> SearchCandidatesAsync(Guid ownerUserId, Guid noteId, string? term, int limit, CancellationToken cancellationToken = default);
}
