using JNotes.Core.Sharing;

namespace JNotes.Core.Data;

/// <summary>
/// One grant: <see cref="OwnerUserId"/> lets <see cref="SharedWithUserId"/> use <see cref="NoteId"/> with
/// <see cref="Permission"/>. The note file itself stays in the owner's folder. Like
/// <see cref="NoteAutoTagAttempt"/> this is NOT derived — it can't be rebuilt from the markdown files, so it
/// is deliberately not <c>NoteIndex*</c>. Cascades with either account; deliberately no FK to
/// <c>NoteIndexEntries</c>, because the index is truncatable. Owned by <c>PostgresNoteShareRepository</c>.
/// </summary>
public sealed class NoteShare
{
    public required Guid NoteId { get; set; }

    public required Guid SharedWithUserId { get; set; }

    public required Guid OwnerUserId { get; set; }

    /// <summary>Stored as the enum's name, never its ordinal.</summary>
    public required NoteSharePermission Permission { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}
