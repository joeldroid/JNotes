namespace JNotes.Core.Data;

/// <summary>
/// One (note, tag) pair — normalized into its own table rather than a CSV column on
/// <see cref="NoteIndexEntry"/> so tag search/filtering can use an indexed join instead of a
/// <c>LIKE</c> scan.
/// </summary>
public sealed class NoteTag
{
    public int Id { get; set; }

    public required Guid NoteId { get; set; }

    public required string Tag { get; set; }
}
