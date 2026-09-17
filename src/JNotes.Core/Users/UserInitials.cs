namespace JNotes.Core.Users;

/// <summary>
/// Computes a short, uppercase, at-most-2-character initials string for an avatar — a plain
/// static class with no DI, the same reasoning <c>NoteGrouping</c> is one for: pure and
/// deterministic, so it's unit-testable without a running Blazor component.
/// </summary>
public static class UserInitials
{
    /// <summary>
    /// Prefers the first letter of <paramref name="firstName"/> and <paramref name="lastName"/>
    /// when both are set; falls back to the first two letters of whichever single name is
    /// available (<paramref name="firstName"/>, then <paramref name="username"/>).
    /// </summary>
    public static string Compute(string? firstName, string? lastName, string username)
    {
        var first = string.IsNullOrWhiteSpace(firstName) ? null : firstName.Trim();
        var last = string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim();

        if (first is not null && last is not null)
        {
            return $"{char.ToUpperInvariant(first[0])}{char.ToUpperInvariant(last[0])}";
        }

        return FirstTwoLetters(first ?? username);
    }

    private static string FirstTwoLetters(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length >= 2
            ? $"{char.ToUpperInvariant(trimmed[0])}{char.ToUpperInvariant(trimmed[1])}"
            : char.ToUpperInvariant(trimmed[0]).ToString();
    }
}
