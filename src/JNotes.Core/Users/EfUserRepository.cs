using JNotes.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Users;

/// <inheritdoc cref="IUserRepository"/>
public sealed class EfUserRepository : IUserRepository
{
    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;
    private readonly DefaultUserProvider _defaultUserProvider;

    public EfUserRepository(IDbContextFactory<JNotesDbContext> dbContextFactory, DefaultUserProvider defaultUserProvider)
    {
        _dbContextFactory = dbContextFactory;
        _defaultUserProvider = defaultUserProvider;
    }

    public async Task<UserAccount> CreateUserAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The unique (NOCASE) index on Username is the only constraint this insert could violate.
            throw new DuplicateUsernameException(username, ex);
        }

        // Keep the cache CookieCurrentUserContext validates against in sync immediately — otherwise
        // a freshly-created user would look "unknown" to any circuit resolved before the next restart.
        _defaultUserProvider.AddKnownUserId(user.Id);

        return user;
    }

    public async Task<IReadOnlyList<UserAccount>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Ordered client-side after materializing: EF Core's SQLite provider can't translate an
        // ORDER BY over a DateTimeOffset column into SQL (see EfNoteIndexRepository.SearchAsync
        // for the same workaround) — fine at this app's scale (a handful of admin-created users).
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);
        return users.OrderBy(u => u.CreatedAt).ToList();
    }

    public async Task<UserAccount?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }
}
