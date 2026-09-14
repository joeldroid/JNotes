using JNotes.Core.Data;
using JNotes.Core.Notes;

namespace JNotes.Core.Indexing;

/// <summary>
/// Thin EF Core CRUD over the SQLite index — no file I/O. <c>NoteService</c> and
/// <c>NoteIndexReconciler</c> are the only callers; everything else goes through them.
/// </summary>
/// <remarks>
/// Every method takes an explicit <c>userId</c> (or, for <see cref="UpsertAsync"/>, reads it off
/// the given <see cref="NoteIndexEntry"/>) for the same reason as <c>INoteFileStore</c>: callers
/// need to address either "the current user" (<c>NoteService</c>) or an arbitrary given user
/// (<c>NoteIndexReconciler</c>), so one explicit parameter serves both.
/// </remarks>
public interface INoteIndexRepository
{
    Task<IReadOnlyList<NoteSummary>> SearchAsync(Guid userId, NoteSearchQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetDistinctTagsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Inserts or fully replaces the index row and tag set for <paramref name="entry"/>.Id, using <paramref name="entry"/>.UserId.</summary>
    Task UpsertAsync(NoteIndexEntry entry, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>No-ops if no row exists for <paramref name="noteId"/> owned by <paramref name="userId"/>.</summary>
    Task DeleteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>Every one of <paramref name="userId"/>'s indexed note ids and the file mtime each was last indexed at — the reconciler's "D" set.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetIndexedFileWriteTimesAsync(Guid userId, CancellationToken cancellationToken = default);
}
