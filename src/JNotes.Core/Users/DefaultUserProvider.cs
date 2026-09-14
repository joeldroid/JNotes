namespace JNotes.Core.Users;

/// <inheritdoc cref="IDefaultUserProvider"/>
public sealed class DefaultUserProvider : IDefaultUserProvider
{
    private readonly object _lock = new();
    private HashSet<Guid> _knownUserIds = [];

    public Guid DefaultUserId { get; private set; }

    /// <summary>Called once by <c>HostExtensions.EnsureDefaultUserAsync</c> at startup, before the app serves any requests.</summary>
    internal void SetDefaultUserId(Guid userId) => DefaultUserId = userId;

    /// <summary>Called once by <c>HostExtensions.EnsureDefaultUserAsync</c> at startup, seeding the full set of existing users.</summary>
    internal void SetKnownUserIds(IEnumerable<Guid> userIds)
    {
        lock (_lock)
        {
            _knownUserIds = [.. userIds];
        }
    }

    /// <summary>Called by <c>EfUserRepository.CreateUserAsync</c> right after a new user is persisted, so the cache never lags a create.</summary>
    internal void AddKnownUserId(Guid userId)
    {
        lock (_lock)
        {
            _knownUserIds.Add(userId);
        }
    }

    public bool IsKnownUser(Guid userId)
    {
        lock (_lock)
        {
            return _knownUserIds.Contains(userId);
        }
    }
}
