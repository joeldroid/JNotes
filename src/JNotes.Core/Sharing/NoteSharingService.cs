using JNotes.Core.Storage;
using JNotes.Core.Users;

namespace JNotes.Core.Sharing;

/// <inheritdoc cref="INoteSharingService"/>
public sealed class NoteSharingService : INoteSharingService
{
    private readonly INoteFileStore _fileStore;
    private readonly INoteShareRepository _shares;
    private readonly IUserRepository _users;
    private readonly ICurrentUserContext _currentUser;

    public NoteSharingService(INoteFileStore fileStore, INoteShareRepository shares, IUserRepository users, ICurrentUserContext currentUser)
    {
        _fileStore = fileStore;
        _shares = shares;
        _users = users;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<NoteShareEntry>> ListSharesAsync(Guid noteId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(noteId, cancellationToken);
        return await _shares.ListForNoteAsync(_currentUser.UserId, noteId, cancellationToken);
    }

    public async Task<IReadOnlyList<ShareRecipientCandidate>> SearchRecipientsAsync(Guid noteId, string? term, int limit = 10, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(noteId, cancellationToken);
        return await _shares.SearchCandidatesAsync(_currentUser.UserId, noteId, term, limit, cancellationToken);
    }

    public async Task ShareAsync(Guid noteId, Guid recipientUserId, NoteSharePermission permission, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(noteId, cancellationToken);

        var me = _currentUser.UserId;
        if (recipientUserId == me)
        {
            throw new InvalidOperationException("You can't share a note with yourself.");
        }

        if (await _users.GetUserByIdAsync(recipientUserId, cancellationToken) is null)
        {
            throw new InvalidOperationException("That user no longer exists.");
        }

        // An upsert, so this is also how a recipient's permission is changed.
        await _shares.UpsertAsync(me, noteId, recipientUserId, permission, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task UnshareAsync(Guid noteId, Guid recipientUserId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(noteId, cancellationToken);
        await _shares.DeleteAsync(_currentUser.UserId, noteId, recipientUserId, cancellationToken);
    }

    // No existence check on purpose: a recipient must be able to leave a note the owner has since trashed.
    public async Task LeaveAsync(Guid noteId, CancellationToken cancellationToken = default) =>
        await _shares.DeleteForRecipientAsync(noteId, _currentUser.UserId, cancellationToken);

    /// <summary>
    /// The owner check for every management call, made in Core on every call. The owner is whoever has the live
    /// file in their own folder; a recipient gets <see cref="NoteAccessDeniedException"/> (they can already see the
    /// note), anyone else <see cref="NoteNotFoundException"/> (no hint that it exists).
    /// </summary>
    private async Task RequireOwnerAsync(Guid noteId, CancellationToken cancellationToken)
    {
        var me = _currentUser.UserId;
        if (await _fileStore.ExistsAsync(me, noteId, cancellationToken))
        {
            return;
        }

        if (await _shares.FindAsync(noteId, me, cancellationToken) is not null)
        {
            throw new NoteAccessDeniedException(noteId, "Only the owner can share this note.");
        }

        throw new NoteNotFoundException(noteId);
    }
}
