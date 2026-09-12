namespace JNotes.Core.Models;

/// <summary>
/// One task-list line parsed out of a <see cref="NoteType.Checklist"/> note's <see cref="Note.Body"/>.
/// This is a read-only projection recomputed from the body on demand — it is never stored separately,
/// so there is only ever one source of truth for checklist state (the markdown text itself).
/// </summary>
/// <param name="LineIndex">The zero-based line number within <see cref="Note.Body"/>, used to target a toggle.</param>
/// <param name="Text">The item's text, with the leading "- [ ] "/"- [x] " markup stripped.</param>
/// <param name="IsChecked">Whether the box is checked (<c>[x]</c>/<c>[X]</c>) or not (<c>[ ]</c>).</param>
public sealed record ChecklistItem(int LineIndex, string Text, bool IsChecked);
