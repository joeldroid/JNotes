using JNotes.Core.Models;
using JNotes.Core.Sharing;

namespace JNotes.Core.Notes;

/// <summary>A note listing row — projected from the note index, never from the file (no <see cref="Note.Body"/>).</summary>
public sealed record NoteSummary(
    Guid Id,
    string Title,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The user whose folder holds the file (the current user for their own notes).</summary>
    public Guid OwnerUserId { get; init; }

    /// <summary>Non-null when the note belongs to someone else and was shared with the current user.</summary>
    public SharedNoteInfo? SharedWithMe { get; init; }

    /// <summary>For the current user's own notes: how many recipients it is shared with. Zero otherwise.</summary>
    public int OutgoingShareCount { get; init; }
}
