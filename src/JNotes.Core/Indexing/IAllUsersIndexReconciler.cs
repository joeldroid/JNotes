namespace JNotes.Core.Indexing;

/// <summary>
/// Reconciles every user's index against their note files in one pass — the shared "loop over
/// everyone" logic behind both startup reconciliation
/// (<c>HostExtensions.RunStartupReconciliationAsync</c>) and the periodic background pass
/// (<c>JNotes.Web.Indexing.AutoIndexBackgroundService</c>), so there's one implementation of that
/// loop and its failure isolation is directly testable without a timer.
/// </summary>
public interface IAllUsersIndexReconciler
{
    Task<AllUsersReconciliationResult> ReconcileAllAsync(CancellationToken cancellationToken = default);
}

public sealed record AllUsersReconciliationResult(
    int UsersReconciled,
    int UsersFailed,
    int Added,
    int Updated,
    int Removed,
    IReadOnlyList<string> Errors);
