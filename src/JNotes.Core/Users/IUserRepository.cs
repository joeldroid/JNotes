using JNotes.Core.Data;

namespace JNotes.Core.Users;

public interface IUserRepository
{
    Task<UserAccount> CreateUserAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Ordered by <see cref="UserAccount.CreatedAt"/> ascending — "first" always means "oldest".</summary>
    Task<IReadOnlyList<UserAccount>> GetAllUsersAsync(CancellationToken cancellationToken = default);

    Task<UserAccount?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
