namespace JNotes.Core.Tagging;

/// <summary>
/// Tags notes that have no tags, in the background, with a local Ollama model. Never overwrites or adds to
/// tags a user wrote, and leaves <c>UpdatedAt</c> alone so notes don't move in the list. Explicit
/// <c>userId</c> rather than <c>ICurrentUserContext</c>, for the same reason as <c>INoteIndexReconciler</c>
/// and <c>IImageCleanupService</c>: the caller is a background service with no current user.
/// </summary>
public interface INoteAutoTagger
{
    /// <summary>
    /// One pass over every user: candidate notes are fetched per user (users in random order), merged
    /// round-robin, and at most <paramref name="maxNotes"/> are attempted, one at a time. Notes changed within
    /// <paramref name="minAge"/> are left for a later pass. A user whose candidates can't be listed is counted
    /// and skipped; an unavailable Ollama ends the pass early (see <see cref="AutoTagPassResult.UnavailableReason"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Auto-tagging isn't enabled (no valid <c>OLLAMA_URL</c>).</exception>
    Task<AutoTagPassResult> TagAllUsersAsync(int maxNotes, TimeSpan minAge, CancellationToken cancellationToken = default);

    /// <summary>The same pass restricted to one user.</summary>
    /// <exception cref="InvalidOperationException">Auto-tagging isn't enabled (no valid <c>OLLAMA_URL</c>).</exception>
    Task<AutoTagPassResult> TagUserNotesAsync(Guid userId, int maxNotes, TimeSpan minAge, CancellationToken cancellationToken = default);
}
