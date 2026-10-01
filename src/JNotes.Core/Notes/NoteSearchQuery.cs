using JNotes.Core.Models;

namespace JNotes.Core.Notes;

/// <summary>Which notes a list/search covers.</summary>
public enum NoteListScope
{
    /// <summary>The current user's own notes plus the notes shared with them.</summary>
    All,

    /// <summary>Only notes other users have shared with the current user.</summary>
    SharedWithMe,
}

public sealed record NoteSearchQuery(string? SearchText = null, string? Tag = null, NoteType? Type = null, NoteListScope Scope = NoteListScope.All);
