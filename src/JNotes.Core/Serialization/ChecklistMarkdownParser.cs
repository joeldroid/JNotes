using System.Text.RegularExpressions;
using JNotes.Core.Models;

namespace JNotes.Core.Serialization;

/// <summary>
/// Reads and toggles markdown task-list lines (<c>- [ ] text</c> / <c>- [x] text</c>) within a
/// <see cref="NoteType.Checklist"/> note's body.
/// </summary>
/// <remarks>
/// Deliberately line-based regex rather than a round trip through Markdig's AST: Markdig is a
/// renderer, not a lossless serializer, so re-emitting markdown from its parsed tree risks subtly
/// reformatting whitespace or content outside the line being edited. Matching one line with a
/// regex and rewriting only its bracket character keeps every other byte of the file untouched.
/// </remarks>
public static partial class ChecklistMarkdownParser
{
    [GeneratedRegex(@"^(?<indent>\s*)-\s\[(?<mark>[ xX])\]\s(?<text>.*)$")]
    private static partial Regex TaskListLineRegex();

    public static IReadOnlyList<ChecklistItem> Parse(string body)
    {
        var lines = SplitLines(body);
        var items = new List<ChecklistItem>();

        for (var i = 0; i < lines.Length; i++)
        {
            var match = TaskListLineRegex().Match(lines[i]);
            if (match.Success)
            {
                items.Add(new ChecklistItem(
                    LineIndex: i,
                    Text: match.Groups["text"].Value,
                    IsChecked: IsCheckedMark(match.Groups["mark"].Value)));
            }
        }

        return items;
    }

    /// <summary>
    /// Flips the checked state of the task-list line at <paramref name="lineIndex"/>. Only that
    /// line's bracket character changes; every other line is returned byte-identical.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lineIndex"/> is outside the body.</exception>
    /// <exception cref="InvalidOperationException">The targeted line is not a checklist item.</exception>
    public static string ToggleItem(string body, int lineIndex)
    {
        var usesCrLf = body.Contains("\r\n", StringComparison.Ordinal);
        var lines = SplitLines(body);

        if (lineIndex < 0 || lineIndex >= lines.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(lineIndex), lineIndex, "Line index is outside the note body.");
        }

        var line = lines[lineIndex];
        var match = TaskListLineRegex().Match(line);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Line {lineIndex} is not a checklist item.");
        }

        var markGroup = match.Groups["mark"];
        var newMark = IsCheckedMark(markGroup.Value) ? " " : "x";
        lines[lineIndex] = string.Concat(line.AsSpan(0, markGroup.Index), newMark, line.AsSpan(markGroup.Index + 1));

        var joined = string.Join('\n', lines);
        return usesCrLf ? joined.Replace("\n", "\r\n") : joined;
    }

    private static bool IsCheckedMark(string mark) => mark is "x" or "X";

    private static string[] SplitLines(string body) => body.Replace("\r\n", "\n").Split('\n');
}
