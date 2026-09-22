using JNotes.Core.Models;

namespace JNotes.Core.Notes;

/// <summary>A note listing row — projected from the note index, never from the file (no <see cref="Note.Body"/>).</summary>
public sealed record NoteSummary(
    Guid Id,
    string Title,
    NoteType Type,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
