namespace JNotes.Core.Notes;

/// <summary>
/// Pure tag-list logic for the tag input (no clock, no I/O), like <c>UserInitials</c>: safe for Web to
/// call directly.
/// </summary>
public static class TagList
{
    /// <summary>Splits <paramref name="text"/> on commas, trims each part and drops blanks.</summary>
    public static IReadOnlyList<string> ParseInput(string? text) =>
        text is null
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// <paramref name="existing"/> plus each parsed tag of <paramref name="toAdd"/> that isn't already
    /// present, compared case-insensitively against <paramref name="existing"/> and against tags added earlier
    /// in the same call. Order and the original spelling are preserved.
    /// </summary>
    public static IReadOnlyList<string> AddTags(IReadOnlyList<string> existing, IEnumerable<string> toAdd)
    {
        var result = new List<string>(existing);
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        foreach (var raw in toAdd)
        {
            foreach (var tag in ParseInput(raw))
            {
                if (seen.Add(tag))
                {
                    result.Add(tag);
                }
            }
        }

        return result;
    }

    /// <summary>Removes the first occurrence of <paramref name="tag"/> by exact (ordinal) match.</summary>
    public static IReadOnlyList<string> RemoveTag(IReadOnlyList<string> existing, string tag)
    {
        var result = new List<string>(existing);
        var index = result.FindIndex(t => string.Equals(t, tag, StringComparison.Ordinal));
        if (index >= 0)
        {
            result.RemoveAt(index);
        }

        return result;
    }
}
