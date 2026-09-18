using JNotes.Core.Users;
using Microsoft.Extensions.Logging;

namespace JNotes.Core.Indexing;

/// <inheritdoc cref="IAllUsersIndexReconciler"/>
public sealed class AllUsersIndexReconciler : IAllUsersIndexReconciler
{
    private readonly IUserRepository _userRepository;
    private readonly INoteIndexReconciler _reconciler;
    private readonly ILogger<AllUsersIndexReconciler> _logger;

    public AllUsersIndexReconciler(IUserRepository userRepository, INoteIndexReconciler reconciler, ILogger<AllUsersIndexReconciler> logger)
    {
        _userRepository = userRepository;
        _reconciler = reconciler;
        _logger = logger;
    }

    public async Task<AllUsersReconciliationResult> ReconcileAllAsync(CancellationToken cancellationToken = default)
    {
        var users = await _userRepository.GetAllUsersAsync(cancellationToken);

        var usersReconciled = 0;
        var usersFailed = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var errors = new List<string>();

        foreach (var user in users)
        {
            // Makes shutdown snappy: RescanAsync threads the token through to the file store/Redis
            // but has no check of its own between users.
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await _reconciler.RescanAsync(user.Id, cancellationToken);
                added += result.Added;
                updated += result.Updated;
                removed += result.Removed;
                errors.AddRange(result.Errors.Select(e => $"{user.Id}: {e}"));
                usersReconciled++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown, not a per-user failure — must propagate, not be swallowed below.
                throw;
            }
            catch (Exception ex)
            {
                // One user's Redis/IO failure must not abort reconciliation for the rest.
                _logger.LogWarning(ex, "Index reconciliation failed for user {UserId}; continuing with the remaining users.", user.Id);
                usersFailed++;
                errors.Add($"{user.Id}: {ex.Message}");
            }
        }

        return new AllUsersReconciliationResult(usersReconciled, usersFailed, added, updated, removed, errors);
    }
}
