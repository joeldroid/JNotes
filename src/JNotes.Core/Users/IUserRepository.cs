using JNotes.Core.Data;

namespace JNotes.Core.Users;

public interface IUserRepository
{
    /// <exception cref="DuplicateUsernameException">The username (case-insensitively) or email is already taken.</exception>
    Task<UserAccount> CreateUserAsync(string username, UserRole role, string? email, string? password, CancellationToken cancellationToken = default);

    /// <summary>Ordered by <see cref="UserAccount.CreatedAt"/> ascending — "first" always means "oldest".</summary>
    Task<IReadOnlyList<UserAccount>> GetAllUsersAsync(CancellationToken cancellationToken = default);

    Task<UserAccount?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<UserAccount?> GetUserByOidcSubjectAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>Matches only an account that has this email AND isn't linked to an OIDC subject yet — never relinks an already-linked account.</summary>
    Task<UserAccount?> GetUnlinkedUserByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Null if the username doesn't exist, has no password set, or the password is wrong — callers shouldn't distinguish which.</summary>
    Task<UserAccount?> VerifyPasswordAsync(string username, string password, CancellationToken cancellationToken = default);

    Task LinkOidcSubjectAsync(Guid userId, string subject, CancellationToken cancellationToken = default);

    /// <summary>Used for both an admin resetting someone's password and a user changing their own.</summary>
    Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes the account row (cascades its notes-index/settings rows) — callers that also need to preserve the user's files should archive them first, see <c>IUserManagementService</c>.</summary>
    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets FirstName/LastName/Email unconditionally — permission (whether the caller is even
    /// allowed to change <paramref name="email"/>) is enforced by <c>IUserManagementService</c>,
    /// not here, the same layering as <c>DeleteUserAsync</c>/<c>IUserManagementService.DeleteUserAsync</c>.
    /// A no-op if <paramref name="userId"/> doesn't exist.
    /// </summary>
    /// <exception cref="DuplicateEmailException"><paramref name="email"/> is already in use by a different account.</exception>
    Task UpdateProfileAsync(Guid userId, string? firstName, string? lastName, string? email, CancellationToken cancellationToken = default);
}
