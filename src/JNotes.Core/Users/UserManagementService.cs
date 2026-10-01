using JNotes.Core.Data;
using JNotes.Core.Storage;

namespace JNotes.Core.Users;

/// <inheritdoc cref="IUserManagementService"/>
public sealed class UserManagementService : IUserManagementService
{
    private readonly IUserRepository _userRepository;
    private readonly INoteFileStore _fileStore;

    public UserManagementService(IUserRepository userRepository, INoteFileStore fileStore)
    {
        _userRepository = userRepository;
        _fileStore = fileStore;
    }

    public async Task DeleteUserAsync(Guid targetUserId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        if (targetUserId == requestingUserId)
        {
            throw new CannotDeleteSelfException();
        }

        var target = await _userRepository.GetUserByIdAsync(targetUserId, cancellationToken);
        if (target is null)
        {
            return; // already gone — tolerate as a no-op success, same as a retried delete
        }

        if (target.Role == UserRole.Admin)
        {
            var admins = (await _userRepository.GetAllUsersAsync(cancellationToken)).Count(u => u.Role == UserRole.Admin);
            if (admins <= 1)
            {
                throw new CannotDeleteLastAdminException();
            }
        }

        // File-move before DB-delete: if the move fails, the account still fully exists and this
        // can just be retried, rather than leaving an index-less orphaned file tree on disk.
        // The row delete below also cascades the user's NoteShares, both as owner and as recipient.
        await _fileStore.ArchiveUserFolderAsync(targetUserId, cancellationToken);
        await _userRepository.DeleteUserAsync(targetUserId, cancellationToken);
    }

    public async Task UpdateProfileAsync(Guid targetUserId, Guid requestingUserId, string? firstName, string? lastName, string? email, CancellationToken cancellationToken = default)
    {
        var requester = await _userRepository.GetUserByIdAsync(requestingUserId, cancellationToken);
        var requesterIsAdmin = requester?.Role == UserRole.Admin;

        if (targetUserId != requestingUserId && !requesterIsAdmin)
        {
            // Not reachable through the UI today (only the admin-gated Users page can target
            // someone else) — defensive, the same belt-and-suspenders DeleteUserAsync already does.
            throw new InvalidOperationException("Only an admin can edit another user's profile.");
        }

        var target = targetUserId == requestingUserId ? requester : await _userRepository.GetUserByIdAsync(targetUserId, cancellationToken);
        if (target is null)
        {
            return; // gone — nothing to update
        }

        if (email is not null && email != target.Email && !requesterIsAdmin)
        {
            throw new InvalidOperationException("Only an admin can change your email.");
        }

        await _userRepository.UpdateProfileAsync(targetUserId, firstName, lastName, email, cancellationToken);
    }
}
