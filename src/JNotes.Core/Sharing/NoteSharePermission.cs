namespace JNotes.Core.Sharing;

/// <summary>What a recipient may do with a shared note. Stored as the enum's name, never its ordinal.</summary>
public enum NoteSharePermission
{
    /// <summary>View only.</summary>
    Read,

    /// <summary>Change the body only — never title, tags, sharing or deletion.</summary>
    Edit,
}
