using JNotes.Core.Models;

namespace JNotes.Core.Storage;

/// <summary>
/// Reads and writes note markdown files on disk, scoped per user (each user's notes live under
/// their own subfolder — see <c>FileSystemNoteFileStore</c>). This is the only place in the app
/// that touches the notes folder — everything above it (indexing, the note service) works through
/// this interface rather than <see cref="System.IO.File"/>/<see cref="System.IO.Directory"/> directly.
/// </summary>
/// <remarks>
/// Every method takes an explicit <paramref name="userId"/> rather than resolving "current user"
/// implicitly, because callers fall into two genuinely different categories: <c>NoteService</c>
/// always operates on "whoever's current" (it resolves that once via <c>ICurrentUserContext</c>
/// and passes it in), while <c>NoteIndexReconciler</c> is a system-level operation with no ambient
/// current user at all — it needs to operate on an arbitrary, explicitly-given user (looped over
/// every user at startup, or a specific one from a "Rescan Now" click). One explicit parameter
/// serves both instead of splitting into two APIs.
/// </remarks>
public interface INoteFileStore
{
    /// <exception cref="NoteNotFoundException">No file exists for <paramref name="noteId"/>.</exception>
    /// <exception cref="Serialization.NoteParseException">The file exists but its front matter can't be parsed.</exception>
    Task<Note> ReadAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>The ids of every note file <paramref name="userId"/> currently has on disk, in no particular order.</summary>
    Task<IReadOnlyList<Guid>> ListIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Creates or overwrites the file for <paramref name="userId"/>'s <paramref name="note"/>.<see cref="Note.Id"/>.</summary>
    Task WriteAsync(Guid userId, Note note, CancellationToken cancellationToken = default);

    /// <summary>No-ops if the file doesn't exist.</summary>
    Task DeleteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <exception cref="NoteNotFoundException">No file exists for <paramref name="noteId"/>.</exception>
    Task<DateTimeOffset> GetLastWriteTimeUtcAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves (never deletes) <paramref name="userId"/>'s entire notes folder to
    /// <c>{NotesFolder}/deleted/{userId}/</c> — used when an admin deletes an account, so nothing
    /// is lost. Idempotent: a no-op if the destination already exists (already archived) or the
    /// source doesn't (the user never wrote a note).
    /// </summary>
    Task ArchiveUserFolderAsync(Guid userId, CancellationToken cancellationToken = default);
}
