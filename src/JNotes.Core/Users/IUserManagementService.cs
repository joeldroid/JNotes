namespace JNotes.Core.Users;

/// <summary>
/// The one place user-deletion's cross-cutting orchestration and guards live — composes
/// <see cref="IUserRepository"/> and <see cref="Storage.INoteFileStore"/>, the same shape
/// <c>NoteService</c> already uses composing the file store and index repository.
/// </summary>
public interface IUserManagementService
{
    /// <summary>
    /// Archives (moves, never deletes) <paramref name="targetUserId"/>'s on-disk notes folder to
    /// <c>{NotesFolder}/deleted/{userId}/</c>, then hard-deletes their account row. The file move
    /// happens first — if it fails, the account still fully exists and the caller can just retry,
    /// rather than an index-less orphaned file tree.
    /// </summary>
    /// <exception cref="CannotDeleteSelfException"><paramref name="targetUserId"/> equals <paramref name="requestingUserId"/>.</exception>
    /// <exception cref="CannotDeleteLastAdminException">The target is the only remaining <see cref="Data.UserRole.Admin"/>.</exception>
    Task DeleteUserAsync(Guid targetUserId, Guid requestingUserId, CancellationToken cancellationToken = default);
}
