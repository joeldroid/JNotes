using JNotes.Core.Tagging;

namespace JNotes.Tests.TestDoubles;

/// <summary>Stands in for <see cref="ITagModelClient"/> (no Ollama in tests): returns <see cref="NextTags"/> and records the prompt it was given.</summary>
internal sealed class RecordingTagModelClient : ITagModelClient
{
    public List<string> NextTags { get; set; } = [];

    public Exception? Throw { get; set; }

    public AutoTagPrompt? LastPrompt { get; private set; }

    public int CallCount { get; private set; }

    public Task<IReadOnlyList<string>> GenerateTagsAsync(AutoTagPrompt prompt, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastPrompt = prompt;
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult<IReadOnlyList<string>>(NextTags.ToList());
    }
}
