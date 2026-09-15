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

    public async Task<Note> ReadAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(userId, noteId, cancellationToken);
        if (!File.Exists(path))
        {
            throw new NoteNotFoundException(noteId);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return _serializer.Deserialize(content);
    }

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var folder = await ResolveFolderAsync(userId, cancellationToken);

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

    public async Task WriteAsync(Guid userId, Note note, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(userId, note.Id, cancellationToken);
        var content = _serializer.Serialize(note);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(userId, noteId, cancellationToken);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task<DateTimeOffset> GetLastWriteTimeUtcAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var path = await ResolvePathAsync(userId, noteId, cancellationToken);
        if (!File.Exists(path))
        {
            throw new NoteNotFoundException(noteId);
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    }

    public async Task ArchiveUserFolderAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var notesRoot = await _settingsService.GetNotesFolderPathAsync(cancellationToken);
        var sourceDir = Path.Combine(notesRoot, userId.ToString());
        var deletedRoot = Path.Combine(notesRoot, "deleted");
        var destDir = Path.Combine(deletedRoot, userId.ToString());

        if (Directory.Exists(destDir))
        {
            return; // already archived (a retried delete after a partial failure) — nothing more to do
        }

        if (!Directory.Exists(sourceDir))
        {
            return; // this user never wrote a note — nothing to archive
        }

        Directory.CreateDirectory(deletedRoot);
        Directory.Move(sourceDir, destDir);
    }

    private async Task<string> ResolveFolderAsync(Guid userId, CancellationToken cancellationToken)
    {
        var notesRoot = await _settingsService.GetNotesFolderPathAsync(cancellationToken);
        var folder = Path.Combine(notesRoot, userId.ToString());
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<string> ResolvePathAsync(Guid userId, Guid noteId, CancellationToken cancellationToken)
    {
        var folder = await ResolveFolderAsync(userId, cancellationToken);
        return Path.Combine(folder, $"{noteId}.md");
    }
}
