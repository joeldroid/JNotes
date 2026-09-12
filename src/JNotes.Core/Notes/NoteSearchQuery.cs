using JNotes.Core.Models;

namespace JNotes.Core.Notes;

public sealed record NoteSearchQuery(string? SearchText = null, string? Tag = null, NoteType? Type = null);
