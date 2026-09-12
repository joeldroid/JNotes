using JNotes.Core.Models;
using JNotes.Core.Serialization;
using JNotes.Core.Settings;

namespace JNotes.Core.Storage;

/// <inheritdoc cref="INoteFileStore"/>
public sealed class FileSystemNoteFileStore : INoteFileStore
{
    private readonly ISettingsService _settingsService;
    private readonly INoteMarkdownSerializer _serializer;

    public FileSystemNoteFileStore(ISettingsService settingsService, INoteMarkdownSerializer serializer)
    {
        _settingsService = settingsService;
        _serializer = serializer;
    }

    public async Task<Note> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(id, cancellationToken);
        if (!File.Exists(path))
        {
            throw new NoteNotFoundException(id);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return _serializer.Deserialize(content);
    }

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken cancellationToken = default)
    {
        var folder = await ResolveFolderAsync(cancellationToken);

        var ids = new List<Guid>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            // A stray .md file not named as a guid isn't one of our notes — ignore it rather than fail.
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public async Task WriteAsync(Note note, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(note.Id, cancellationToken);
        var content = _serializer.Serialize(note);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(id, cancellationToken);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task<DateTimeOffset> GetLastWriteTimeUtcAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(id, cancellationToken);
        if (!File.Exists(path))
        {
            throw new NoteNotFoundException(id);
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    }

    private async Task<string> ResolveFolderAsync(CancellationToken cancellationToken)
    {
        var folder = await _settingsService.GetNotesFolderPathAsync(cancellationToken);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<string> ResolvePathAsync(Guid id, CancellationToken cancellationToken)
    {
        var folder = await ResolveFolderAsync(cancellationToken);
        return Path.Combine(folder, $"{id}.md");
    }
}
