using JNotes.Core.Data;
using JNotes.Core.Serialization;
using JNotes.Core.Storage;

namespace JNotes.Core.Indexing;

/// <inheritdoc cref="INoteIndexReconciler"/>
public sealed class NoteIndexReconciler : INoteIndexReconciler
{
    private readonly INoteFileStore _fileStore;
    private readonly INoteIndexRepository _indexRepository;

    public NoteIndexReconciler(INoteFileStore fileStore, INoteIndexRepository indexRepository)
    {
        _fileStore = fileStore;
        _indexRepository = indexRepository;
    }

    public async Task<ReconciliationResult> RescanAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var fileIds = await _fileStore.ListIdsAsync(userId, cancellationToken);
        var indexedWriteTimes = await _indexRepository.GetIndexedFileWriteTimesAsync(userId, cancellationToken);

        var fileIdSet = fileIds.ToHashSet();
        var added = 0;
        var updated = 0;
        var removed = 0;
        var errors = new List<string>();

        foreach (var id in fileIds)
        {
            var isNew = !indexedWriteTimes.TryGetValue(id, out var lastIndexedWriteTime);

            DateTimeOffset currentWriteTime;
            try
            {
                currentWriteTime = await _fileStore.GetLastWriteTimeUtcAsync(userId, id, cancellationToken);
            }
            catch (NoteNotFoundException)
            {
                // Deleted between ListIdsAsync and here — the next rescan converges on it either way.
                continue;
            }

            if (!isNew && lastIndexedWriteTime >= currentWriteTime)
            {
                continue; // Unchanged since it was last indexed — skip re-parsing the file.
            }

            try
            {
                var note = await _fileStore.ReadAsync(userId, id, cancellationToken);
                var entry = new NoteIndexEntry
                {
                    Id = note.Id,
                    UserId = userId,
                    Title = note.Title,
                    NoteType = note.Type.ToString(),
                    CreatedAt = note.CreatedAt,
                    UpdatedAt = note.UpdatedAt,
                    IndexedFileWriteTimeUtc = currentWriteTime,
                    FilePath = $"{userId}/{note.Id}.md",
                };
                await _indexRepository.UpsertAsync(entry, note.Tags, cancellationToken);

                if (isNew)
                {
                    added++;
                }
                else
                {
                    updated++;
                }
            }
            catch (NoteParseException ex)
            {
                // Leave any existing index row as-is so the note stays visible in listings
                // instead of silently vanishing because of a corrupted file.
                errors.Add($"{id}: {ex.Message}");
            }
        }

        foreach (var id in indexedWriteTimes.Keys.Except(fileIdSet))
        {
            await _indexRepository.DeleteAsync(userId, id, cancellationToken);
            removed++;
        }

        return new ReconciliationResult(added, updated, removed, errors);
    }
}
