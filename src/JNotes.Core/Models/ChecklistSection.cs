namespace JNotes.Core.Models;

/// <summary>
/// A category within a <see cref="NoteType.Checklist"/> note: a markdown heading line and the task-list
/// items beneath it, up to the next heading. Like <see cref="ChecklistItem"/> this is a read-only
/// projection recomputed from the body on demand, never stored separately.
/// </summary>
/// <param name="HeadingLineIndex">The zero-based line number of the heading, or <c>null</c> for the
/// uncategorized section (items before the first heading, or the whole list when there are no headings).</param>
/// <param name="Title">The heading's text without its <c>#</c> markup, or <c>null</c> for the uncategorized section.</param>
/// <param name="Items">The section's items, in file order. May be empty (a category with nothing in it yet).</param>
public sealed record ChecklistSection(int? HeadingLineIndex, string? Title, IReadOnlyList<ChecklistItem> Items);
