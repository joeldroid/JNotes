namespace JNotes.Core.Data;

/// <summary>
/// Stored as its name (<c>HasConversion&lt;string&gt;()</c> in <c>JNotesDbContext</c>), not the
/// default int ordinal — reordering this enum must never silently reassign who's an admin.
/// </summary>
public enum UserRole
{
    Admin,
    Member,
}
