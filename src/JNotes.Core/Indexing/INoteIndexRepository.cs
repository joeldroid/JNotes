using JNotes.Core.Data;
using JNotes.Core.Notes;

namespace JNotes.Core.Indexing;

/// <summary>
/// Thin EF Core CRUD over the SQLite index — no file I/O. <c>NoteService</c> and
/// <c>NoteIndexReconciler</c> are the only callers; everything else goes through them.
/// </summary>
public interface INoteIndexRepository
{
    Task<IReadOnlyList<NoteSummary>> SearchAsync(NoteSearchQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetDistinctTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or fully replaces the index row and tag set for <paramref name="entry"/>.Id.</summary>
    Task UpsertAsync(NoteIndexEntry entry, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>No-ops if no row exists for <paramref name="id"/>.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every indexed note's id and the file mtime it was last indexed at — the reconciler's "D" set.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetIndexedFileWriteTimesAsync(CancellationToken cancellationToken = default);
}
