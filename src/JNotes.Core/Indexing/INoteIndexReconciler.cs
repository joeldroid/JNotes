namespace JNotes.Core.Indexing;

/// <summary>
/// Reconciles the SQLite index against the note files actually on disk, so edits made outside the
/// app (or a crash mid-write) are self-healed. Run once at startup and on demand from the
/// Settings page's "Rescan Now" — there is no <see cref="System.IO.FileSystemWatcher"/> watching
/// the folder live; that's unreliable over Docker bind mounts across host platforms, so it's left
/// as a deferred enhancement rather than attempted for MVP.
/// </summary>
public interface INoteIndexReconciler
{
    Task<ReconciliationResult> RescanAsync(CancellationToken cancellationToken = default);
}
