namespace JNotes.Core.Indexing;

/// <summary>
/// The indexed metadata for one note — mirrors the note file for fast listing/search. The
/// markdown file is still the source of truth: this is a derived cache kept in sync by
/// <c>NoteService</c> (on write) and <c>INoteIndexReconciler</c> (reconciling against the
/// filesystem), never edited from anywhere else. Also the EF entity behind the
/// <c>NoteIndexEntries</c> table; its tags live in the child <c>NoteIndexTags</c> table (see
/// <see cref="JNotes.Core.Data.NoteIndexTag"/>) rather than on this class.
/// </summary>
public sealed class NoteIndexEntry
{
    public required Guid Id { get; set; }

    public required Guid UserId { get; set; }

    public required string Title { get; set; }

    /// <summary>
    /// A copy of the note's body, kept here purely so search can match on content without opening
    /// every file — never rendered from here (<c>NoteService.GetAsync</c> still reads the file for
    /// that). Every save through <c>NoteService</c> updates it, including a
    /// <see cref="Notes.NoteSaveScope.ContentOnly"/> autosave (via
    /// <c>INoteIndexRepository.UpdateContentAsync</c>); it only lags behind edits made outside the app,
    /// a save whose index write failed, or a title/tag edit not yet saved at <c>Metadata</c> scope,
    /// until the next reconciliation pass.
    /// </summary>
    public required string Body { get; set; }

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
