namespace JNotes.Core.Data;

/// <summary>
/// The SQLite-indexed metadata for one note — mirrors the note file for fast listing/search.
/// Never holds body content: the markdown file is the source of truth for that, this is a
/// derived cache kept in sync by <c>NoteService</c> (on write) and <c>INoteIndexReconciler</c>
/// (reconciling against the filesystem).
/// </summary>
public sealed class NoteIndexEntry
{
    public required Guid Id { get; set; }

    public required string Title { get; set; }

    /// <summary>Stored as the string name of <see cref="Models.NoteType"/> (not an int) so a future
    /// reordering of the enum can't silently corrupt existing rows.</summary>
    public required string NoteType { get; set; }

    /// <summary>The note's own logical timestamp, from its front matter.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>The note's own logical timestamp, from its front matter.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The file's on-disk mtime as observed the last time this row was (re)indexed — distinct from
    /// <see cref="UpdatedAt"/>. Reconciliation compares this against the file's current mtime to
    /// decide whether a file needs re-parsing, without opening/parsing every file on every rescan.
    /// </summary>
    public required DateTimeOffset IndexedFileWriteTimeUtc { get; set; }

    /// <summary>
    /// Technically derivable (<c>{Id}.md</c> in the current flat layout), but stored explicitly so
    /// the index stays meaningful if storage ever stops being a single flat folder, and so the UI
    /// can offer "reveal on disk" without recomputing the convention.
    /// </summary>
    public required string FilePath { get; set; }
}
