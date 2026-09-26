using System.Text;
using System.Text.RegularExpressions;

namespace JNotes.Core.Tagging;

/// <summary>
/// Turns the model's raw tags into ones worth adding: lowercase kebab-case (valid Obsidian tag syntax, so
/// files stay portable), no generic or duplicate tags, and — when a tag matches one the user already uses —
/// the user's own spelling, because <c>NoteIndexUserTags</c> and the tag filter are case-sensitive and
/// "Work"/"work" would otherwise become two tags. Pure, like <c>TagList</c>.
/// </summary>
public static partial class AutoTagNormalizer
{
    public const int MaxTags = 5;
    public const int MaxTagLength = 40;

    private const string StrippedChars = ",;\"'`[]{}()<>";

    private static readonly HashSet<string> GenericTags = new(StringComparer.Ordinal)
    {
        "note", "notes", "text", "checklist", "todo", "to-do", "list", "markdown", "misc", "general", "untitled",
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"[\s_-]+")]
    private static partial Regex SeparatorRun();

    [GeneratedRegex("-{2,}")]
    private static partial Regex HyphenRun();

    /// <summary>Comparison key: lowercase with runs of whitespace/<c>_</c>/<c>-</c> collapsed to <c>-</c>.</summary>
    public static string Key(string tag) =>
        SeparatorRun().Replace(tag.Trim().ToLowerInvariant(), "-");

    /// <summary>The cleaned-up tag, or <c>null</c> when it should be dropped (empty, too long, generic).</summary>
    public static string? NormalizeOne(string raw)
    {
        var cleaned = new StringBuilder();
        foreach (var c in raw.Trim().TrimStart('#'))
        {
            if (!char.IsControl(c) && !StrippedChars.Contains(c))
            {
                cleaned.Append(c);
            }
        }

        var tag = WhitespaceRun().Replace(cleaned.ToString().Trim(), "-").ToLowerInvariant();
        tag = HyphenRun().Replace(tag, "-").Trim('-', '.', '/');

        if (tag.Length == 0 || tag.Length > MaxTagLength || GenericTags.Contains(tag))
        {
            return null;
        }

        return tag;
    }

    /// <summary>
    /// Normalised, de-duplicated tags in the model's order, excluding those already on the note
    /// (<paramref name="currentTags"/>, by <see cref="Key"/>) and preferring the spelling in
    /// <paramref name="vocabulary"/>; at most <paramref name="maxTags"/>.
    /// </summary>
    public static IReadOnlyList<string> Normalize(
        IEnumerable<string> raw,
        IReadOnlyCollection<string> currentTags,
        IReadOnlyCollection<string> vocabulary,
        int maxTags = MaxTags)
    {
        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in vocabulary)
        {
            spellings.TryAdd(Key(tag), tag);
        }

        var seen = new HashSet<string>(currentTags.Select(Key), StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var candidate in raw)
        {
            if (result.Count >= maxTags)
            {
                break;
            }

            if (NormalizeOne(candidate) is not { } tag)
            {
                continue;
            }

            var key = Key(tag);
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(spellings.GetValueOrDefault(key, tag));
        }

        return result;
    }
}
