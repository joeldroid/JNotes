namespace JNotes.Core.Notes;

/// <summary>A trashed-note listing row — read directly from its file (trash has no index; see <c>NoteService.ListTrashAsync</c>).</summary>
public sealed record TrashedNoteSummary(
    Guid Id,
    string Title,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt,
    DateTimeOffset TrashedAt);
