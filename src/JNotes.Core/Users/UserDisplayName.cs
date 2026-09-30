namespace JNotes.Core.Users;

/// <summary>
/// The name shown for a user where others see it ("Shared by …", the share dialog) — a plain static
/// class like <see cref="UserInitials"/>, pure and unit-testable.
/// </summary>
public static class UserDisplayName
{
    /// <summary>The trimmed "First Last" when either part is non-blank, otherwise <paramref name="username"/>.</summary>
    public static string Format(string? firstName, string? lastName, string username)
    {
        var first = firstName?.Trim() ?? "";
        var last = lastName?.Trim() ?? "";
        var full = $"{first} {last}".Trim();
        return full.Length > 0 ? full : username;
    }
}
