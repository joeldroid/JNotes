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

            // Rounded to the index's own resolution before comparing or storing: PostgreSQL timestamptz keeps
            // microseconds while the filesystem mtime has 100-nanosecond ticks, so an un-rounded comparison
            // would make every unchanged file look modified on every rescan. See IndexTimestamp.
            currentWriteTime = IndexTimestamp.ToIndexResolution(currentWriteTime);

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
                    Body = note.Body,
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

        // Heals a truncated or drifted NoteIndexUserTags even when every file is unchanged: the mtime skip
        // above would otherwise never touch it.
        await _indexRepository.RebuildUserTagsAsync(userId, cancellationToken);

        return new ReconciliationResult(added, updated, removed, errors);
    }
}
