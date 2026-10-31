namespace JNotes.Core.Notes;

/// <summary>Which notes a list/search covers.</summary>
public enum NoteListScope
{
    /// <summary>The current user's own notes plus the notes shared with them.</summary>
    All,

    /// <summary>Only notes other users have shared with the current user.</summary>
    SharedWithMe,
}

/// <param name="SearchText">Free text matched against title, body and tags.</param>
/// <param name="Tag">Exact, case-sensitive tag filter.</param>
/// <param name="Scope">Which notes the list covers.</param>
/// <param name="Limit">At most this many rows, newest first, applied in SQL after every filter; <c>null</c> = no limit; must be &gt;= 1.</param>
public sealed record NoteSearchQuery(string? SearchText = null, string? Tag = null, NoteListScope Scope = NoteListScope.All, int? Limit = null);
