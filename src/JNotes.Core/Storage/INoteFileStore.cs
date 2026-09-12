using JNotes.Core.Models;

namespace JNotes.Core.Storage;

/// <summary>
/// Reads and writes note markdown files on disk. This is the only place in the app that touches
/// the notes folder — everything above it (indexing, the note service) works through this
/// interface rather than <see cref="System.IO.File"/>/<see cref="System.IO.Directory"/> directly.
/// </summary>
public interface INoteFileStore
{
    /// <exception cref="NoteNotFoundException">No file exists for <paramref name="id"/>.</exception>
    /// <exception cref="Serialization.NoteParseException">The file exists but its front matter can't be parsed.</exception>
    Task<Note> ReadAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The ids of every note file currently on disk, in no particular order.</summary>
    Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or overwrites the file for <paramref name="note"/>.<see cref="Note.Id"/>.</summary>
    Task WriteAsync(Note note, CancellationToken cancellationToken = default);

    /// <summary>No-ops if the file doesn't exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <exception cref="NoteNotFoundException">No file exists for <paramref name="id"/>.</exception>
    Task<DateTimeOffset> GetLastWriteTimeUtcAsync(Guid id, CancellationToken cancellationToken = default);
}
