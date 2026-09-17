using System.Text.RegularExpressions;

namespace JNotes.Core.Users;

/// <summary>
/// Username/email format rules shared by every account-creation path (first-run <c>/setup</c>,
/// the admin "Add user" form) — a plain static class with no DI, the same reasoning
/// <c>ChecklistMarkdownParser</c>/<c>UserInitials</c> are ones for: pure and deterministic, so
/// it's unit-testable without EF Core.
/// </summary>
public static partial class UserValidation
{
    // Deliberately permissive (not full RFC 5322) — good enough to catch typos/malformed input
    // without rejecting valid-but-unusual addresses; this app never actually sends mail to verify one.
    public const string EmailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";

    [GeneratedRegex(EmailPattern)]
    private static partial Regex EmailRegex();

    public static bool IsValidEmail(string email) => EmailRegex().IsMatch(email);

    /// <summary>
    /// Usernames are already compared case-insensitively (the <c>Username</c> column uses NOCASE
    /// collation), but must be *typed* in lowercase — otherwise "Alice" and "alice" would render
    /// differently everywhere despite being the same account underneath.
    /// </summary>
    public static bool IsLowercase(string username) => username == username.ToLowerInvariant();
}
