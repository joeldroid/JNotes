namespace JNotes.Core.Notes;

/// <summary>One relative-date section of the notes list — see <see cref="NoteGrouping.GroupByRelativeDate"/>.</summary>
public sealed record NoteGroup(string Label, IReadOnlyList<NoteSummary> Notes);
