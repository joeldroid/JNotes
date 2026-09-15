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
        await _fileStore.ArchiveUserFolderAsync(targetUserId, cancellationToken);
        await _userRepository.DeleteUserAsync(targetUserId, cancellationToken);
    }
}
