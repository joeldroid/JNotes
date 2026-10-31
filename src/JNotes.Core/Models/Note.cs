namespace JNotes.Core.Models;

/// <summary>
/// A note as read from (or about to be written to) its markdown file. This is the persistence-agnostic
/// domain model: it has no file path or storage details of its own — <c>{NotesFolder}/{Id}.md</c> is
/// derived by the file store, not carried on this type.
/// </summary>
public sealed class Note
{
    public required Guid Id { get; init; }

    public required string Title { get; set; }

    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The markdown content after the YAML front matter block.</summary>
    public required string Body { get; set; }
}
