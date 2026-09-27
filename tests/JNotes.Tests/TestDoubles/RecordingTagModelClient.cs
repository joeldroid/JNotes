using JNotes.Core.Tagging;

namespace JNotes.Tests.TestDoubles;

/// <summary>
/// Stands in for <see cref="ITagModelClient"/> (no Ollama in tests): returns <see cref="NextTags"/>, records the prompt it
/// was given, and can run <see cref="OnCall"/> first (to change a file "while the model is thinking") or throw <see cref="Throw"/>.
/// </summary>
internal sealed class RecordingTagModelClient : ITagModelClient
{
    public List<string> NextTags { get; set; } = [];

    public Exception? Throw { get; set; }

    /// <summary>Awaited on every call, before <see cref="Throw"/> or the result.</summary>
    public Func<AutoTagPrompt, Task>? OnCall { get; set; }

    public AutoTagPrompt? LastPrompt { get; private set; }

    public int CallCount { get; private set; }

    public async Task<IReadOnlyList<string>> GenerateTagsAsync(AutoTagPrompt prompt, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastPrompt = prompt;
        if (OnCall is not null)
        {
            await OnCall(prompt);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        return NextTags.ToList();
    }

    /// <summary>Back to a fresh state, including <see cref="CallCount"/>.</summary>
    public void Reset()
    {
        NextTags = [];
        Throw = null;
        OnCall = null;
        LastPrompt = null;
        CallCount = 0;
    }
}
