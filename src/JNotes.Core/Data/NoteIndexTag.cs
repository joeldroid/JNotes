namespace JNotes.Core.Data;

/// <summary>
/// One tag on one indexed note. A child table rather than a jsonb/text[] column because search has
/// to match title OR body OR *any tag* with ILIKE in a single query, which needs an EXISTS
/// subquery — jsonb_array_elements_text isn't expressible in LINQ, and Npgsql's text[] mapping has
/// no "any element ILIKE" translation. It also turns the exact-tag filter and the distinct-tag
/// listing into indexed SQL instead of loading every note for the user into memory. The pre-Redis
/// SQLite index had a join table for the same reason.
/// Ordinal preserves the note's own front-matter tag order (what the note list renders), and — like
/// the JSON array it replaces — allows duplicates.
/// Rows are owned entirely by INoteIndexRepository.UpsertAsync/DeleteAsync and cascade with their
/// NoteIndexEntry; like everything else in the index, they're rebuildable from the files on disk.
/// </summary>
public sealed class NoteIndexTag
{
    public required Guid NoteIndexEntryId { get; set; }

    public required int Ordinal { get; set; }

    public required string Tag { get; set; }
}
