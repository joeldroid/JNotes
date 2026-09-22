using JNotes.Core.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Users;

/// <inheritdoc cref="IUserRepository"/>
public sealed class EfUserRepository : IUserRepository
{
    private readonly IDbContextFactory<JNotesDbContext> _dbContextFactory;
    private readonly PasswordHasher<UserAccount> _passwordHasher;
    private readonly SetupStateProvider _setupStateProvider;

    public EfUserRepository(
        IDbContextFactory<JNotesDbContext> dbContextFactory,
        PasswordHasher<UserAccount> passwordHasher,
        SetupStateProvider setupStateProvider)
    {
        _dbContextFactory = dbContextFactory;
        _passwordHasher = passwordHasher;
        _setupStateProvider = setupStateProvider;
    }

    public async Task<UserAccount> CreateUserAsync(string username, UserRole role, string? email, string? password, CancellationToken cancellationToken = default)
    {
        // Enforced here (not just in the UI) so every creation path — /setup, the admin "Add
        // user" form, and any future caller — gets the same guarantee, the same belt-and-suspenders
        // reasoning as UpdateProfileAsync's email-permission check.
        if (!UserValidation.IsLowercase(username))
        {
            throw new InvalidUsernameException(username);
        }

        if (email is not null && !UserValidation.IsValidEmail(email))
        {
            throw new InvalidEmailException(email);
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Checked proactively (rather than relying solely on the two unique-index violations that
        // would otherwise collide inside one generic DbUpdateException) so callers get the specific,
        // correct exception — a tiny check-then-insert race remains, acceptable at this app's
        // low-traffic single-instance scale, same trade-off already made elsewhere in this app.
        if (await db.Users.AnyAsync(u => u.Username == username, cancellationToken))
        {
            throw new DuplicateUsernameException(username, new InvalidOperationException("Username already exists."));
        }

        if (email is not null && await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new DuplicateEmailException(email);
        }

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            CreatedAt = DateTimeOffset.UtcNow,
            Role = role,
            Email = email,
        };
        user.PasswordHash = password is null ? null : _passwordHasher.HashPassword(user, password);

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Backstop for the check-then-insert race above — can't tell which index actually
            // fired here, so this is deliberately the less specific of the two exception types.
            throw new DuplicateUsernameException(username, ex);
        }

        // "Setup complete" means "at least one user exists" — true after any successful create,
        // not just the very first (idempotent to set again). Flip the in-memory flag right away so
        // a request that lands between this write and the next restart doesn't get bounced to
        // /setup as if no admin existed yet.
        _setupStateProvider.SetSetupComplete(true);

        return user;
    }

    public async Task<IReadOnlyList<UserAccount>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Ordered in SQL: PostgreSQL orders timestamptz natively (the SQLite provider couldn't translate
        // an ORDER BY over a DateTimeOffset column, which is why this used to materialize first).
        return await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<UserAccount?> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public async Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Username's case-insensitive ICU collation (see JNotesDbContext) makes this comparison case-insensitive already.
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
    }

    public async Task<UserAccount?> GetUserByOidcSubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.OidcSubject == subject, cancellationToken);
    }

    public async Task<UserAccount?> GetUnlinkedUserByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email && u.OidcSubject == null, cancellationToken);
    }

    public async Task<UserAccount?> VerifyPasswordAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user?.PasswordHash is null)
        {
            return null;
        }

        var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }

    public async Task LinkOidcSubjectAsync(Guid userId, string subject, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return;
        }

        user.OidcSubject = subject;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return;
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return; // already gone — treat as success, matches IUserManagementService's retry tolerance
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateProfileAsync(Guid userId, string? firstName, string? lastName, string? email, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return;
        }

        // Same proactive-check pattern as CreateUserAsync — only when the email is actually
        // changing, so saving a profile back with its own unchanged email never false-positives.
        if (email is not null && email != user.Email && await db.Users.AnyAsync(u => u.Id != userId && u.Email == email, cancellationToken))
        {
            throw new DuplicateEmailException(email);
        }

        user.FirstName = firstName;
        user.LastName = lastName;
        user.Email = email;
        await db.SaveChangesAsync(cancellationToken);
    }
}
