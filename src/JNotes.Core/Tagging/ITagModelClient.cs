namespace JNotes.Core.Tagging;

/// <summary>The low-level call to the language model — one prompt in, raw tags out. Implemented over HTTP by <see cref="OllamaTagModelClient"/>.</summary>
public interface ITagModelClient
{
    /// <summary>The model's raw, un-normalised tags.</summary>
    /// <exception cref="AutoTagException">The model was unreachable, timed out, errored or replied with something unusable.</exception>
    Task<IReadOnlyList<string>> GenerateTagsAsync(AutoTagPrompt prompt, CancellationToken cancellationToken = default);
}
