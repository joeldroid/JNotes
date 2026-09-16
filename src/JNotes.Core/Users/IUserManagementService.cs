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

    /// <summary>
    /// Updates <paramref name="targetUserId"/>'s profile. FirstName/LastName are always allowed for
    /// your own profile, or any profile if you're an Admin. Email is Admin-only to change — a
    /// Member can never change their own email, by design (see /profile), enforced here rather
    /// than just in the UI so it can't be bypassed by a client that skips the disabled input.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="requestingUserId"/> isn't an Admin and either targets someone else, or is
    /// trying to change <paramref name="email"/>.
    /// </exception>
    /// <exception cref="DuplicateEmailException"><paramref name="email"/> is already in use by a different account.</exception>
    Task UpdateProfileAsync(Guid targetUserId, Guid requestingUserId, string? firstName, string? lastName, string? email, CancellationToken cancellationToken = default);
}
