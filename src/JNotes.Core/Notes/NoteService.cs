using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Models;
using JNotes.Core.Serialization;
using JNotes.Core.Storage;
using JNotes.Core.Users;

namespace JNotes.Core.Notes;

/// <inheritdoc cref="INoteService"/>
public sealed class NoteService : INoteService
{
    private readonly INoteFileStore _fileStore;
    private readonly INoteIndexRepository _indexRepository;
    private readonly ICurrentUserContext _currentUser;

    public NoteService(INoteFileStore fileStore, INoteIndexRepository indexRepository, ICurrentUserContext currentUser)
    {
        _fileStore = fileStore;
        _indexRepository = indexRepository;
        _currentUser = currentUser;
    }

    public Task<IReadOnlyList<NoteSummary>> ListAsync(NoteSearchQuery query, CancellationToken cancellationToken = default) =>
        _indexRepository.SearchAsync(_currentUser.UserId, query, cancellationToken);

    public Task<IReadOnlyList<string>> GetDistinctTagsAsync(CancellationToken cancellationToken = default) =>
        _indexRepository.GetDistinctTagsAsync(_currentUser.UserId, cancellationToken);

    public Task<Note> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _fileStore.ReadAsync(_currentUser.UserId, id, cancellationToken);

    public async Task<Note> CreateAsync(NoteType type, string title, IEnumerable<string> tags, string body, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            Title = title,
            Type = type,
            Tags = tags.ToArray(),
            CreatedAt = now,
            UpdatedAt = now,
            Body = body,
        };

        await WriteAndIndexAsync(note, cancellationToken);
        return note;
    }

    public async Task<Note> UpdateAsync(Note note, CancellationToken cancellationToken = default)
    {
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await WriteAndIndexAsync(note, cancellationToken);
        return note;
    }

    public async Task<Note> ToggleChecklistItemAsync(Guid noteId, int lineIndex, CancellationToken cancellationToken = default)
    {
        var note = await _fileStore.ReadAsync(_currentUser.UserId, noteId, cancellationToken);
        if (note.Type != NoteType.Checklist)
        {
            throw new InvalidOperationException($"Note '{noteId}' is not a checklist note.");
        }

        note.Body = ChecklistMarkdownParser.ToggleItem(note.Body, lineIndex);
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await WriteAndIndexAsync(note, cancellationToken);
        return note;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // File first, then index: files are the source of truth, so a crash in between leaves a
        // stale index row pointing at a missing file — the next reconciliation removes it. The
        // reverse order could leave a file with no index row that nothing would ever notice.
        await _fileStore.DeleteAsync(_currentUser.UserId, id, cancellationToken);
        await _indexRepository.DeleteAsync(_currentUser.UserId, id, cancellationToken);
    }

    private async Task WriteAndIndexAsync(Note note, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId;

        // File first, then index: a crash in between leaves a file the next reconciliation will
        // discover and index, never an index entry pointing at a file that was never written.
        await _fileStore.WriteAsync(userId, note, cancellationToken);
        var writeTimeUtc = await _fileStore.GetLastWriteTimeUtcAsync(userId, note.Id, cancellationToken);

        var entry = new NoteIndexEntry
        {
            Id = note.Id,
            UserId = userId,
            Title = note.Title,
            NoteType = note.Type.ToString(),
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
            IndexedFileWriteTimeUtc = writeTimeUtc,
            // Relative to the notes folder, not absolute: NoteService doesn't know the folder
            // setting, and an absolute path would go stale if it's ever changed.
            FilePath = $"{userId}/{note.Id}.md",
        };

        await _indexRepository.UpsertAsync(entry, note.Tags, cancellationToken);
    }
}
