using System.Text.RegularExpressions;
using JNotes.Core.Models;

namespace JNotes.Core.Serialization;

/// <summary>
/// Reads, checks/unchecks, and appends markdown task-list lines (<c>- [ ] text</c> / <c>- [x] text</c>)
/// within a <see cref="NoteType.Checklist"/> note's body.
/// </summary>
/// <remarks>
/// Deliberately line-based regex rather than a round trip through Markdig's AST: Markdig is a
/// renderer, not a lossless serializer, so re-emitting markdown from its parsed tree risks subtly
/// reformatting whitespace or content outside the line being edited. Matching one line with a
/// regex and rewriting only its bracket character keeps every line's own bytes untouched.
/// <para>
/// Checking or unchecking an item also moves its line within its checklist block (see
/// <see cref="BlockBounds"/>) so checked items end up after unchecked ones — physically in the file,
/// so the markdown reads the way the app renders it. The block is stably partitioned (unchecked, then
/// checked, each keeping its relative order) and the toggled item goes last in its destination group.
/// Lines are only reordered, never re-emitted from a parsed tree.
/// </para>
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
    /// Sets the task-list line at <paramref name="lineIndex"/> to <paramref name="isChecked"/> and
    /// moves it within its checklist block so checked items sort after unchecked ones. Every line's
    /// own text is preserved byte-for-byte; only the target's bracket character and line order change.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lineIndex"/> is outside the body.</exception>
    /// <exception cref="InvalidOperationException">The targeted line is not a checklist item.</exception>
    public static string SetItemChecked(string body, int lineIndex, bool isChecked)
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
        var newMark = isChecked ? "x" : " ";
        var target = string.Concat(line.AsSpan(0, markGroup.Index), newMark, line.AsSpan(markGroup.Index + 1));

        var (start, endExclusive) = BlockBounds(lines, lineIndex, match.Groups["indent"].Value);

        // Stable partition of the block minus the target, then the target goes last in whichever
        // group it now belongs to (see the class remarks for why).
        var rest = new List<string>();
        for (var i = start; i < endExclusive; i++)
        {
            if (i != lineIndex)
            {
                rest.Add(lines[i]);
            }
        }

        var uncheckedLines = rest.Where(l => !IsLineChecked(l));
        var checkedLines = rest.Where(IsLineChecked);
        var reordered = isChecked
            ? uncheckedLines.Concat(checkedLines).Append(target)
            : uncheckedLines.Append(target).Concat(checkedLines);

        var position = start;
        foreach (var reorderedLine in reordered)
        {
            lines[position++] = reorderedLine;
        }

        return Join(lines, usesCrLf);
    }

    /// <summary>
    /// Appends one unchecked task-list line per non-blank line of <paramref name="text"/> to the last
    /// checklist block in <paramref name="body"/> (above any checked items in it), or to the end of
    /// the body if it has no checklist lines. Each line is trimmed; no escaping is needed because the
    /// task-list regex reads everything after the marker to end-of-line as the item's text.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null, empty, or only whitespace/blank lines.</exception>
    public static string AppendItems(string body, string text)
    {
        var texts = (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (texts.Count == 0)
        {
            throw new ArgumentException("Item text must contain at least one non-blank line.", nameof(text));
        }

        var usesCrLf = body.Contains("\r\n", StringComparison.Ordinal);
        var lines = SplitLines(body).ToList();

        foreach (var itemText in texts)
        {
            var (insertAt, indent) = AppendPoint(lines);
            lines.Insert(insertAt, $"{indent}- [ ] {itemText}");
        }

        return Join(lines, usesCrLf);
    }

    /// <summary>
    /// A checklist block is a maximal run of consecutive task-list lines sharing the same indent
    /// capture; any other line (prose, heading, blank, or a task line at a different indent) ends it.
    /// </summary>
    private static (int Start, int EndExclusive) BlockBounds(IReadOnlyList<string> lines, int lineIndex, string indent)
    {
        var start = lineIndex;
        while (start > 0 && IsTaskLineWithIndent(lines[start - 1], indent))
        {
            start--;
        }

        var endExclusive = lineIndex + 1;
        while (endExclusive < lines.Count && IsTaskLineWithIndent(lines[endExclusive], indent))
        {
            endExclusive++;
        }

        return (start, endExclusive);
    }

    private static bool IsTaskLineWithIndent(string line, string indent)
    {
        var match = TaskListLineRegex().Match(line);
        return match.Success && match.Groups["indent"].Value == indent;
    }

    private static bool IsLineChecked(string line)
    {
        var match = TaskListLineRegex().Match(line);
        return match.Success && IsCheckedMark(match.Groups["mark"].Value);
    }

    /// <summary>
    /// Where the next new item goes: the end of the last block's unchecked run, or — when there are
    /// no task lines at all — the end of the body, ahead of any trailing blank lines so an existing
    /// trailing newline survives.
    /// </summary>
    private static (int InsertAt, string Indent) AppendPoint(List<string> lines)
    {
        var last = lines.FindLastIndex(l => TaskListLineRegex().IsMatch(l));
        if (last < 0)
        {
            var index = lines.Count;
            while (index > 0 && lines[index - 1].Length == 0)
            {
                index--;
            }

            return (index, string.Empty);
        }

        var indent = TaskListLineRegex().Match(lines[last]).Groups["indent"].Value;
        var (start, endExclusive) = BlockBounds(lines, last, indent);

        var insertAt = start;
        while (insertAt < endExclusive && !IsLineChecked(lines[insertAt]))
        {
            insertAt++;
        }

        return (insertAt, indent);
    }

    private static string Join(IEnumerable<string> lines, bool usesCrLf)
    {
        var joined = string.Join('\n', lines);
        return usesCrLf ? joined.Replace("\n", "\r\n") : joined;
    }

    private static bool IsCheckedMark(string mark) => mark is "x" or "X";

    private static string[] SplitLines(string body) => body.Replace("\r\n", "\n").Split('\n');
}
