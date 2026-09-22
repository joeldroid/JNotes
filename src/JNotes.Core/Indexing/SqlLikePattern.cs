using System.Text;

namespace JNotes.Core.Indexing;

/// <summary>
/// Turns a user's free-text search term into a safe ILIKE pattern. Exists because %, _ and \ are
/// wildcards in SQL LIKE but were ordinary characters under the Redis index's
/// string.Contains(..., OrdinalIgnoreCase) — searching for "100%" or "snake_case" must keep
/// meaning what it says.
/// </summary>
public static class SqlLikePattern
{
    /// <summary>The ESCAPE character used with every pattern this class produces.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>The same character as a string, for EF.Functions.ILike's escapeCharacter parameter.</summary>
    public const string EscapeString = "\\";

    /// <summary>Escapes <paramref name="text"/>'s LIKE wildcards and wraps it in %…% — a substring match.</summary>
    public static string ContainsPattern(string text) => $"%{Escape(text)}%";

    /// <summary>Escapes \, % and _ with <see cref="EscapeCharacter"/>. The backslash must be escaped first.</summary>
    public static string Escape(string text)
    {
        // The backslash must be replaced first — doing % or _ first would double-escape the
        // backslashes those replacements introduce.
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '%' or '_')
            {
                builder.Append(EscapeCharacter);
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
