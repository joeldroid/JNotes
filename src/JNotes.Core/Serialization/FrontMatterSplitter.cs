namespace JNotes.Core.Serialization;

/// <summary>
/// Splits a note file's raw text into its YAML front matter block and the markdown body that
/// follows it. Front matter must be the very first thing in the file: a "---" line, the YAML,
/// then a closing "---" line — the same convention used by Jekyll/Hugo/most static site generators.
/// </summary>
/// <remarks>
/// This is a small hand-rolled splitter rather than a walk over Markdig's AST: front matter
/// detection here only needs "does the file start with a delimited block", which a couple of
/// string searches answer directly and predictably, without depending on how a general-purpose
/// markdown parser's block boundaries behave across versions/edge cases. Markdig itself is still
/// used, just for read-only preview rendering of the body (see <c>IMarkdownRenderer</c>), not
/// for structural parsing of the file.
/// </remarks>
internal static class FrontMatterSplitter
{
    private const string Delimiter = "---";

    public static (string YamlText, string Body) Split(string fileContent)
    {
        var normalized = fileContent.Replace("\r\n", "\n");

        if (!normalized.StartsWith(Delimiter + "\n", StringComparison.Ordinal))
        {
            throw new NoteParseException("Note file must start with a '---' YAML front matter block.");
        }

        var yamlStart = Delimiter.Length + 1;
        var closingNewlineIndex = normalized.IndexOf("\n" + Delimiter, yamlStart, StringComparison.Ordinal);
        if (closingNewlineIndex < 0)
        {
            throw new NoteParseException("Note file's YAML front matter block is not closed with '---'.");
        }

        var yamlText = normalized[yamlStart..closingNewlineIndex];

        var bodyStart = closingNewlineIndex + 1 + Delimiter.Length;
        var body = bodyStart < normalized.Length
            ? normalized[bodyStart..].TrimStart('\n')
            : string.Empty;

        return (yamlText, body);
    }

    public static string Join(string yamlText, string body) =>
        $"{Delimiter}\n{yamlText.TrimEnd('\n')}\n{Delimiter}\n{body}";
}
