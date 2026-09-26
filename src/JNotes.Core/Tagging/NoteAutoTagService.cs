using JNotes.Core.Models;
using JNotes.Core.Notes;
using Microsoft.Extensions.Options;

namespace JNotes.Core.Tagging;

/// <inheritdoc cref="INoteAutoTagService"/>
public sealed class NoteAutoTagService(
    INoteService notes,
    ITagModelClient model,
    IOptions<AutoTagOptions> options) : INoteAutoTagService
{
    public async Task<IReadOnlyList<string>> GenerateTagsAsync(
        NoteType type,
        string? title,
        string? body,
        IReadOnlyCollection<string> currentTags,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsEnabled)
        {
            throw new InvalidOperationException("Auto-tagging is not enabled (AUTO_TAG_ENABLED/OLLAMA_URL).");
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        // The current user's most-used tags (INoteService scopes this) — so the model reuses their spelling.
        var vocabulary = await notes.SuggestTagsAsync(null, [], AutoTagPromptBuilder.MaxVocabulary, cancellationToken);

        var prompt = AutoTagPromptBuilder.Build(type, title, body, currentTags, vocabulary);
        var raw = await model.GenerateTagsAsync(prompt, cancellationToken);
        return AutoTagNormalizer.Normalize(raw, currentTags, vocabulary);
    }
}
