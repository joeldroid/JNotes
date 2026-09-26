using JNotes.Core.Models;

namespace JNotes.Core.Tagging;

/// <summary>
/// Suggests tags for a note's content. Implicit current user (only to read that user's tag vocabulary, via
/// <c>INoteService</c>), like <c>INoteExportService</c>. Reads no files and writes nothing: it takes the
/// editor's in-memory content (a draft has no file; an existing note may be ahead of its file by a debounce)
/// and the caller merges the result into its own tags.
/// </summary>
public interface INoteAutoTagService
{
    /// <summary>Normalised tags not already in <paramref name="currentTags"/> (at most <see cref="AutoTagNormalizer.MaxTags"/>); empty when there's nothing to classify.</summary>
    /// <exception cref="AutoTagException">The model was unreachable, timed out, errored or replied with something unusable.</exception>
    /// <exception cref="InvalidOperationException">Auto-tagging isn't enabled.</exception>
    Task<IReadOnlyList<string>> GenerateTagsAsync(
        NoteType type,
        string? title,
        string? body,
        IReadOnlyCollection<string> currentTags,
        CancellationToken cancellationToken = default);
}
