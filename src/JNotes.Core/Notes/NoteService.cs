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

    public Task<Note> UpdateAsync(Note note, CancellationToken cancellationToken = default) =>
        AutosaveAsync(note, NoteSaveScope.Metadata, cancellationToken);

    public async Task<Note> ToggleChecklistItemAsync(Guid noteId, int lineIndex, CancellationToken cancellationToken = default)
    {
        var note = await _fileStore.ReadAsync(_currentUser.UserId, noteId, cancellationToken);
        if (note.Type != NoteType.Checklist)
        {
            throw new InvalidOperationException($"Note '{noteId}' is not a checklist note.");
        }

        note.Body = ChecklistMarkdownParser.ToggleItem(note.Body, lineIndex);
        // A checkbox toggle is a body edit like any other — it doesn't need the index either.
        return await AutosaveAsync(note, NoteSaveScope.ContentOnly, cancellationToken);
    }

    public async Task<Note> AutosaveAsync(Note note, NoteSaveScope scope, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        note.UpdatedAt = DateTimeOffset.UtcNow;

        if (scope == NoteSaveScope.ContentOnly)
        {
            // File only — deliberately doesn't touch NoteIndexReconciler.IndexedFileWriteTimeUtc,
            // so this is indistinguishable from "a file edited outside the app" to the reconciler,
            // which already knows how to catch the index up (Title/UpdatedAt/tags) the next time it
            // runs (startup, or Settings -> Rescan Now). No new sync mechanism needed — see
            // NoteIndexReconciler.RescanAsync.
            await _fileStore.WriteAsync(userId, note, cancellationToken);
            return note;
        }

        await WriteAndIndexAsync(note, cancellationToken);
        return note;
    }

    public async Task TrashAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // File first, then index: mirrors the old hard-delete's reasoning — a crash in between
        // leaves a stale index row pointing at a file that's now in the trash; the next
        // reconciliation removes it (ListIdsAsync never sees the trash subfolder, so a trashed
        // note looks identical to a deleted one from the reconciler's point of view).
        await _fileStore.TrashAsync(_currentUser.UserId, id, cancellationToken);
        await _indexRepository.DeleteAsync(_currentUser.UserId, id, cancellationToken);
    }

    public async Task<IReadOnlyList<TrashedNoteSummary>> ListTrashAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        var ids = await _fileStore.ListTrashedIdsAsync(userId, cancellationToken);

        var summaries = new List<TrashedNoteSummary>();
        foreach (var id in ids)
        {
            try
            {
                var note = await _fileStore.ReadTrashedAsync(userId, id, cancellationToken);
                var trashedAt = await _fileStore.GetTrashedAtUtcAsync(userId, id, cancellationToken);
                summaries.Add(new TrashedNoteSummary(note.Id, note.Title, note.Type, note.Tags, note.UpdatedAt, trashedAt));
            }
            catch (NoteNotFoundException)
            {
                // Purged (e.g. from another tab) between ListTrashedIdsAsync and here — skip it,
                // the same race tolerance ListAsync's reconciler already relies on elsewhere.
            }
            catch (NoteParseException)
            {
                // Corrupted front matter — leave it out of the list rather than fail the whole
                // page; the file is still sitting in the trash folder for manual recovery.
            }
        }

        return summaries.OrderByDescending(s => s.TrashedAt).ToList();
    }

    public async Task<Note> RestoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;

        // File first, then index — same reasoning as everywhere else: a crash after the move but
        // before reindexing just leaves a live file the next reconciliation picks up on its own.
        await _fileStore.RestoreAsync(userId, id, cancellationToken);
        var note = await _fileStore.ReadAsync(userId, id, cancellationToken);
        var writeTimeUtc = await _fileStore.GetLastWriteTimeUtcAsync(userId, id, cancellationToken);
        await IndexAsync(note, writeTimeUtc, cancellationToken);
        return note;
    }

    public Task PurgeAsync(Guid id, CancellationToken cancellationToken = default) =>
        _fileStore.PurgeAsync(_currentUser.UserId, id, cancellationToken);

    private async Task WriteAndIndexAsync(Note note, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId;

        // File first, then index: a crash in between leaves a file the next reconciliation will
        // discover and index, never an index entry pointing at a file that was never written.
        await _fileStore.WriteAsync(userId, note, cancellationToken);
        var writeTimeUtc = await _fileStore.GetLastWriteTimeUtcAsync(userId, note.Id, cancellationToken);
        await IndexAsync(note, writeTimeUtc, cancellationToken);
    }

    private async Task IndexAsync(Note note, DateTimeOffset writeTimeUtc, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId;

        var entry = new NoteIndexEntry
        {
            Id = note.Id,
            UserId = userId,
            Title = note.Title,
            Body = note.Body,
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
