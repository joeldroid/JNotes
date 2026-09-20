using System.Runtime.CompilerServices;
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

    public async Task TrashAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var sourcePath = await ResolvePathAsync(userId, noteId, cancellationToken);
        if (!File.Exists(sourcePath))
        {
            return; // already trashed, or a retried trash after a partial failure — nothing to move
        }

        var destPath = await ResolveTrashPathAsync(userId, noteId, cancellationToken);
        File.Move(sourcePath, destPath, overwrite: true);
        // Same-volume moves don't touch mtime — stamp it explicitly so it means "when this was
        // trashed" for GetTrashedAtUtcAsync, the same way a fresh write's mtime means "last edited".
        File.SetLastWriteTimeUtc(destPath, DateTime.UtcNow);
    }

    public async Task RestoreAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var sourcePath = await ResolveTrashPathAsync(userId, noteId, cancellationToken);
        if (!File.Exists(sourcePath))
        {
            return; // not in the trash — nothing to restore
        }

        var destPath = await ResolvePathAsync(userId, noteId, cancellationToken);
        File.Move(sourcePath, destPath);
    }

    public async Task PurgeAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var path = await ResolveTrashPathAsync(userId, noteId, cancellationToken);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task<IReadOnlyList<Guid>> ListTrashedIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var folder = await ResolveTrashFolderAsync(userId, cancellationToken);

        var ids = new List<Guid>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public async Task<Note> ReadTrashedAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var path = await ResolveTrashPathAsync(userId, noteId, cancellationToken);
        if (!File.Exists(path))
        {
            throw new NoteNotFoundException(noteId);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return _serializer.Deserialize(content);
    }

    public async Task<DateTimeOffset> GetTrashedAtUtcAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var path = await ResolveTrashPathAsync(userId, noteId, cancellationToken);
        if (!File.Exists(path))
        {
            throw new NoteNotFoundException(noteId);
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    }

    public async IAsyncEnumerable<string> EnumerateRawContentsAsync(
        Guid userId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var folder = await ResolveFolderAsync(userId, cancellationToken);
        var trashFolder = await ResolveTrashFolderAsync(userId, cancellationToken);

        foreach (var file in Directory.EnumerateFiles(folder, "*.md").Concat(Directory.EnumerateFiles(trashFolder, "*.md")))
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                continue; // purged or trashed since we listed it
            }

            yield return content;
        }
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

    private async Task<string> ResolveTrashFolderAsync(Guid userId, CancellationToken cancellationToken)
    {
        var folder = await ResolveFolderAsync(userId, cancellationToken);
        var trashFolder = Path.Combine(folder, "trash");
        Directory.CreateDirectory(trashFolder);
        return trashFolder;
    }

    private async Task<string> ResolveTrashPathAsync(Guid userId, Guid noteId, CancellationToken cancellationToken)
    {
        var folder = await ResolveTrashFolderAsync(userId, cancellationToken);
        return Path.Combine(folder, $"{noteId}.md");
    }
}
