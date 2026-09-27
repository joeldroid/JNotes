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

    /// <summary>The exact text of the note's file, unparsed.</summary>
    /// <exception cref="NoteNotFoundException">No file exists for <paramref name="noteId"/>.</exception>
    Task<string> ReadRawAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>The ids of every note file <paramref name="userId"/> currently has on disk, in no particular order.</summary>
    Task<IReadOnlyList<Guid>> ListIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Creates or overwrites the file for <paramref name="userId"/>'s <paramref name="note"/>.<see cref="Note.Id"/>.</summary>
    Task WriteAsync(Guid userId, Note note, CancellationToken cancellationToken = default);

    /// <summary>
    /// A compare-and-swap for background writers: overwrites the file with <paramref name="replacement"/>
    /// only if its current text is exactly <paramref name="expectedContent"/> (ordinal), holding an
    /// exclusive handle across the read, compare and write. Returns <c>false</c> — writing nothing — if the
    /// file is missing or its text differs, i.e. somebody changed it since the caller read it.
    /// </summary>
    /// <exception cref="IOException">Another handle has the file open (a sharing violation); nothing was written.</exception>
    Task<bool> ReplaceIfUnchangedAsync(Guid userId, Guid noteId, string expectedContent, Note replacement, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Moves <paramref name="noteId"/>'s file to <c>{userId}/trash/{noteId}.md</c> and stamps its
    /// mtime to now — that stamp is what <see cref="GetTrashedAtUtcAsync"/> later reports as "when
    /// this was deleted", since a same-volume move alone wouldn't otherwise touch mtime. No-ops if
    /// the file doesn't exist (already trashed, or a retried trash after a partial failure).
    /// </summary>
    Task TrashAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>Moves <paramref name="noteId"/>'s file back out of the trash. No-ops if it isn't there.</summary>
    Task RestoreAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes <paramref name="noteId"/>'s file from the trash. No-ops if it isn't there.</summary>
    Task PurgeAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>The ids of every note currently in <paramref name="userId"/>'s trash, in no particular order.</summary>
    Task<IReadOnlyList<Guid>> ListTrashedIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <exception cref="NoteNotFoundException">No file exists in the trash for <paramref name="noteId"/>.</exception>
    Task<Note> ReadTrashedAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>When <paramref name="noteId"/> was moved to the trash — see <see cref="TrashAsync"/>.</summary>
    /// <exception cref="NoteNotFoundException">No file exists in the trash for <paramref name="noteId"/>.</exception>
    Task<DateTimeOffset> GetTrashedAtUtcAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The raw, unparsed text of every <c>.md</c> file <paramref name="userId"/> has — live notes and
    /// the trash alike, including ones whose front matter is corrupted or whose name isn't a note id.
    /// For scans that must not miss anything (which images are still referenced), where going through
    /// <see cref="ReadAsync"/> would skip exactly the files it can't parse. A file that vanishes
    /// mid-scan (purged, trashed) is skipped; any other I/O failure propagates.
    /// </summary>
    IAsyncEnumerable<string> EnumerateRawContentsAsync(Guid userId, CancellationToken cancellationToken = default);
}
