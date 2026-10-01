namespace JNotes.Core.Sharing;

/// <summary>
/// Managing who a note is shared with. Implicit current user, like <c>INoteService</c>: only the note's owner
/// may list, add, change or remove recipients; a recipient may only <see cref="LeaveAsync"/>. Note reads and
/// writes by recipients live on <c>INoteService</c>.
/// </summary>
public interface INoteSharingService
{
    /// <summary>The note's recipients, ordered by display name. Owner only.</summary>
    /// <exception cref="NoteAccessDeniedException">The caller is a recipient, not the owner.</exception>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">The caller has no access to the note.</exception>
    Task<IReadOnlyList<NoteShareEntry>> ListSharesAsync(Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Users the owner could still share the note with (everyone but the owner and current recipients) whose username or
    /// name contains <paramref name="term"/>. Owner only. Never includes or matches an email address.
    /// </summary>
    /// <exception cref="NoteAccessDeniedException">The caller is a recipient, not the owner.</exception>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">The caller has no access to the note.</exception>
    Task<IReadOnlyList<ShareRecipientCandidate>> SearchRecipientsAsync(Guid noteId, string? term, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>Shares the note with <paramref name="recipientUserId"/> at <paramref name="permission"/>, or changes an existing grant's permission. Owner only.</summary>
    /// <exception cref="InvalidOperationException">The recipient is the caller, or no longer exists.</exception>
    /// <exception cref="NoteAccessDeniedException">The caller is a recipient, not the owner.</exception>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">The caller has no access to the note.</exception>
    Task ShareAsync(Guid noteId, Guid recipientUserId, NoteSharePermission permission, CancellationToken cancellationToken = default);

    /// <summary>Removes <paramref name="recipientUserId"/>'s access. Owner only; a no-op if they had none.</summary>
    /// <exception cref="NoteAccessDeniedException">The caller is a recipient, not the owner.</exception>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">The caller has no access to the note.</exception>
    Task UnshareAsync(Guid noteId, Guid recipientUserId, CancellationToken cancellationToken = default);

    /// <summary>Removes the caller's own access to a note shared with them. Works even while the owner has the note in the trash; a no-op if they have none.</summary>
    Task LeaveAsync(Guid noteId, CancellationToken cancellationToken = default);
}
