namespace JNotes.Core.Users;

/// <summary>
/// A cached, startup-populated view of user accounts, kept in sync as users are created. Exists so
/// a per-circuit <see cref="ICurrentUserContext"/> implementation (e.g. the temporary, cookie-based
/// <c>CookieCurrentUserContext</c>) can resolve "current user" with zero database I/O — reading
/// these cached values is safe to do from a DI constructor, unlike an async EF Core query.
/// </summary>
public interface IDefaultUserProvider
{
    /// <summary>The id of the oldest user account, cached at startup by <c>HostExtensions.EnsureDefaultUserAsync</c>.</summary>
    Guid DefaultUserId { get; }

    /// <summary>
    /// Whether <paramref name="userId"/> is (as far as this cache knows) a real, existing user —
    /// used to fall back to <see cref="DefaultUserId"/> instead of trusting an unsigned cookie that
    /// names a user who's been deleted or never existed (e.g. a leftover cookie from before a
    /// fresh-start database reset).
    /// </summary>
    bool IsKnownUser(Guid userId);
}
