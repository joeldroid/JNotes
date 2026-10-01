namespace JNotes.Core.Images;

/// <summary>
/// How an uploaded image is referenced from a note. In the <c>.md</c> file it's a link relative to
/// the note (<c>images/{file}</c> — images live in <c>{userId}/images/</c>, right beside the note
/// files), so the raw folder still renders in Obsidian/VS Code/GitHub. The in-app renderer rewrites
/// it to <see cref="ServedPrefix"/>, an authenticated route. The inserter and the renderer share
/// these definitions so the two can't drift apart.
/// </summary>
public static class ImageLinks
{
    public const string FolderName = "images";
    public const string RelativePrefix = FolderName + "/";
    public const string ServedPrefix = "/" + FolderName + "/";

    /// <summary>The markdown to insert for a stored image; alt text is the original file name without its extension.</summary>
    public static string ToMarkdown(string originalFileName, string storedFileName) =>
        $"![{ToAltText(originalFileName)}]({RelativePrefix}{storedFileName})";

    /// <summary>
    /// The URL to render for a markdown image URL, or <c>null</c> to leave it alone — only an exact
    /// <c>images/{valid stored name}</c> is rewritten, so external URLs and anything odd pass through untouched.
    /// </summary>
    public static string? ToServedUrl(string markdownUrl) =>
        ToStoredFileName(markdownUrl) is { } fileName ? ServedPrefix + fileName : null;

    /// <summary>The note-scoped route prefix (<c>/notes/{id}/images/</c>) a non-owner's images are served from.</summary>
    public static string NoteScopedPrefix(Guid noteId) => $"/notes/{noteId}/{FolderName}/";

    /// <summary>Like <see cref="ToServedUrl"/>, but for the note-scoped route a recipient of <paramref name="noteId"/> reads through.</summary>
    public static string? ToNoteScopedServedUrl(string markdownUrl, Guid noteId) =>
        ToStoredFileName(markdownUrl) is { } fileName ? NoteScopedPrefix(noteId) + fileName : null;

    /// <summary>
    /// The stored file name in a markdown image URL, or <c>null</c> — only an exact
    /// <c>images/{valid stored name}</c> qualifies.
    /// </summary>
    public static string? ToStoredFileName(string markdownUrl)
    {
        if (!markdownUrl.StartsWith(RelativePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = markdownUrl[RelativePrefix.Length..];
        return ImageFormat.IsStoredFileName(fileName) ? fileName : null;
    }

    private static string ToAltText(string originalFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        // Brackets/parens would end the alt text or the link early; control characters (line
        // breaks) would break the markdown line.
        var cleaned = new string(stem.Where(c => c is not ('[' or ']' or '(' or ')') && !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "image" : cleaned;
    }
}
