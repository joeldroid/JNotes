namespace JNotes.Core.Data;

/// <summary>
/// A derived per-user projection of NoteIndexTags: one row per (user, distinct tag), used for tag
/// auto-suggest and the distinct-tag listing. Owned entirely by PostgresNoteIndexRepository — recomputed
/// for the affected tags in UpsertAsync/DeleteAsync (same transaction as the NoteIndexTags write) and
/// fully rebuilt by RebuildUserTagsAsync at the end of every NoteIndexReconciler.RescanAsync. Safe to
/// TRUNCATE. Deliberately has no FK to Users, like NoteIndexEntries.
/// NoteCount is the number of distinct indexed notes carrying the tag; LastUsedAt is the max UpdatedAt
/// of those notes.
/// </summary>
public sealed class NoteIndexUserTag
{
    public required Guid UserId { get; set; }

    public required string Tag { get; set; }

    public required int NoteCount { get; set; }

    public required DateTimeOffset LastUsedAt { get; set; }
}
