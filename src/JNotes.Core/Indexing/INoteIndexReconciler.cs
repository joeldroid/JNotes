namespace JNotes.Core.Indexing;

/// <summary>
/// Reconciles one user's index rows against their note files actually on disk, so edits
/// made outside the app (or a crash mid-write) are self-healed. Run once per user at startup
/// (looped by <c>HostExtensions.RunStartupReconciliationAsync</c>) and on demand from the
/// Settings page's "Rescan Now" (scoped to whoever clicked it) — there is no
/// <see cref="System.IO.FileSystemWatcher"/> watching folders live; that's unreliable over Docker
/// bind mounts across host platforms, so it's left as a deferred enhancement rather than
/// attempted for MVP.
/// </summary>
/// <remarks>
/// Takes an explicit <c>userId</c> rather than an implicit "current user": this runs at startup,
/// before any request/circuit (and so before there's any "current user" to resolve), looped over
/// every user — see <c>INoteFileStore</c>'s remarks for the same reasoning applied there.
/// </remarks>
public interface INoteIndexReconciler
{
    Task<ReconciliationResult> RescanAsync(Guid userId, CancellationToken cancellationToken = default);
}
