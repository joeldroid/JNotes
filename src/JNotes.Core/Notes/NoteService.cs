using JNotes.Core.Images;
using JNotes.Core.Indexing;
using JNotes.Core.Models;
using JNotes.Core.Serialization;
using JNotes.Core.Sharing;
using JNotes.Core.Storage;
using JNotes.Core.Users;

namespace JNotes.Core.Notes;

/// <inheritdoc cref="INoteService"/>
public sealed class NoteService : INoteService
{
    // How often a recipient's compare-and-swap write is retried when the owner's file changed under it.
    private const int MaxReplaceAttempts = 3;

    private readonly INoteFileStore _fileStore;
    private readonly INoteIndexRepository _indexRepository;
    private readonly ICurrentUserContext _currentUser;
    private readonly INoteShareRepository _shares;
    private readonly IImageStore _imageStore;
    private readonly INoteMarkdownSerializer _serializer;

    public NoteService(
        INoteFileStore fileStore,
        INoteIndexRepository indexRepository,
        ICurrentUserContext currentUser,
        INoteShareRepository shares,
        IImageStore imageStore,
        INoteMarkdownSerializer serializer)
    {
        _fileStore = fileStore;
        _indexRepository = indexRepository;
        _currentUser = currentUser;
        _shares = shares;
        _imageStore = imageStore;
        _serializer = serializer;
    }

    public Task<IReadOnlyList<NoteSummary>> ListAsync(NoteSearchQuery query, CancellationToken cancellationToken = default) =>
        _indexRepository.SearchAsync(_currentUser.UserId, query, cancellationToken);

    public Task<IReadOnlyList<string>> GetDistinctTagsAsync(CancellationToken cancellationToken = default) =>
        _indexRepository.GetDistinctTagsAsync(_currentUser.UserId, cancellationToken);

    public Task<IReadOnlyList<string>> SuggestTagsAsync(string? term, IReadOnlyCollection<string> exclude, int limit = 10, CancellationToken cancellationToken = default) =>
        _indexRepository.SuggestTagsAsync(_currentUser.UserId, term, exclude, limit, cancellationToken);

    public async Task<Note> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var access = await ResolveAccessAsync(id, cancellationToken);
        return await _fileStore.ReadAsync(access.OwnerUserId, id, cancellationToken);
    }

    public async Task<NoteWithAccess> GetWithAccessAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var access = await ResolveAccessAsync(id, cancellationToken);
        var note = await _fileStore.ReadAsync(access.OwnerUserId, id, cancellationToken);
        return new NoteWithAccess(note, access);
    }

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

        await WriteAndIndexAsync(_currentUser.UserId, note, cancellationToken);
        return note;
    }

    public Task<Note> UpdateAsync(Note note, CancellationToken cancellationToken = default) =>
        AutosaveAsync(note, NoteSaveScope.Metadata, cancellationToken);

    public Task<Note> SetChecklistItemCheckedAsync(Guid noteId, int lineIndex, bool isChecked, CancellationToken cancellationToken = default) =>
        // A checkbox change is a body edit like any other: ContentOnly, a one-row index update.
        MutateChecklistBodyAsync(noteId, body => ChecklistMarkdownParser.SetItemChecked(body, lineIndex, isChecked), cancellationToken);

    public Task<Note> AddChecklistItemAsync(Guid noteId, string text, int? categoryHeadingLine = null, CancellationToken cancellationToken = default) =>
        // Same reasoning as SetChecklistItemCheckedAsync: body only, so ContentOnly.
        MutateChecklistBodyAsync(noteId, body => ChecklistMarkdownParser.AppendItemsToCategory(body, text, categoryHeadingLine), cancellationToken);

    public Task<Note> AddChecklistCategoryAsync(Guid noteId, string name, CancellationToken cancellationToken = default) =>
        MutateChecklistBodyAsync(noteId, body => ChecklistMarkdownParser.AddCategory(body, name), cancellationToken);

    /// <summary>
    /// Applies <paramref name="mutate"/> to a checklist note's body, as its owner (read, mutate, ContentOnly autosave)
    /// or as an Edit recipient (a body-only compare-and-swap merge on the owner's file).
    /// </summary>
    private async Task<Note> MutateChecklistBodyAsync(Guid noteId, Func<string, string> mutate, CancellationToken cancellationToken)
    {
        var access = await ResolveAccessAsync(noteId, cancellationToken);
        RequireBodyEdit(access, noteId);

        if (access.IsOwner)
        {
            var note = await _fileStore.ReadAsync(access.OwnerUserId, noteId, cancellationToken);
            RequireChecklist(note);
            note.Body = mutate(note.Body);
            return await AutosaveAsync(note, NoteSaveScope.ContentOnly, cancellationToken);
        }

        var merged = await ReplaceBodyAsync(
            access.OwnerUserId,
            noteId,
            current =>
            {
                RequireChecklist(current);
                current.Body = mutate(current.Body);
            },
            cancellationToken);
        await IndexSavedAsync(access.OwnerUserId, merged, NoteSaveScope.ContentOnly, cancellationToken);
        return merged;
    }

    private static void RequireChecklist(Note note)
    {
        if (note.Type != NoteType.Checklist)
        {
            throw new InvalidOperationException($"Note '{note.Id}' is not a checklist note.");
        }
    }

    public async Task<Note> AutosaveAsync(Note note, NoteSaveScope scope, CancellationToken cancellationToken = default)
    {
        var access = await ResolveAccessAsync(note.Id, cancellationToken);
        RequireBodyEdit(access, note.Id);
        var ownerId = access.OwnerUserId;

        if (!access.IsOwner)
        {
            // An Edit recipient never goes through WriteAsync (which would create the file if it were missing): the
            // owner's current file is merged with only the caller's Body — see ReplaceBodyAsync.
            var merged = await ReplaceBodyAsync(
                ownerId,
                note.Id,
                current =>
                {
                    if (current.Type != note.Type)
                    {
                        throw new NoteAccessDeniedException(note.Id, "Only the owner can change a note's type.");
                    }

                    current.Body = note.Body;
                },
                cancellationToken);

            await IndexSavedAsync(ownerId, merged, scope, cancellationToken);
            return merged;
        }

        note.UpdatedAt = DateTimeOffset.UtcNow;

        if (scope == NoteSaveScope.ContentOnly)
        {
            // File first, then a one-row index update (Body/UpdatedAt only), so search and list order see the
            // edit straight away. Title/tags and the indexed mtime are deliberately left alone: the editor
            // saves its whole in-memory note here, so a title/tag edit may be riding along ahead of its own
            // Metadata save, and the lagging mtime lets NoteIndexReconciler.RescanAsync re-parse and heal it.
            // The same reconciler heals the case where this index write fails after the file is saved.
            await _fileStore.WriteAsync(ownerId, note, cancellationToken);
            await IndexSavedAsync(ownerId, note, scope, cancellationToken);
            return note;
        }

        await WriteAndIndexAsync(ownerId, note, cancellationToken);
        return note;
    }

    public async Task TrashAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Own folder only: a recipient's id is a natural no-op here (nothing in their folder to move, no index
        // row of theirs to delete), so no access check is needed.
        //
        // File first, then index: mirrors the old hard-delete's reasoning — a crash in between
        // leaves a stale index row pointing at a file that's now in the trash; the next
        // reconciliation removes it (ListIdsAsync never sees the trash subfolder, so a trashed
        // note looks identical to a deleted one from the reconciler's point of view).
        // The note's share rows are kept: with the file gone from the live folder and the index row deleted,
        // recipients can neither list nor read it until it is restored.
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
        // Own folder only (a recipient's id is a natural no-op/NoteNotFoundException). Reindexing brings the
        // note's still-present share rows back into recipients' lists.
        var userId = _currentUser.UserId;

        // File first, then index — same reasoning as everywhere else: a crash after the move but
        // before reindexing just leaves a live file the next reconciliation picks up on its own.
        await _fileStore.RestoreAsync(userId, id, cancellationToken);
        var note = await _fileStore.ReadAsync(userId, id, cancellationToken);
        var writeTimeUtc = await _fileStore.GetLastWriteTimeUtcAsync(userId, id, cancellationToken);
        await IndexAsync(userId, note, writeTimeUtc, cancellationToken);
        return note;
    }

    public async Task PurgeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        await _fileStore.PurgeAsync(userId, id, cancellationToken);

        // File first, then rows: an orphan share row left by a crash is invisible (no file, no index row) and harmless.
        // Only when the note isn't live, so a purge of a live note's id can never strip its shares.
        if (!await _fileStore.ExistsAsync(userId, id, cancellationToken))
        {
            await _shares.DeleteAllForNoteAsync(userId, id, cancellationToken);
        }
    }

    public async Task<ImageContent?> OpenImageAsync(Guid noteId, string fileName, CancellationToken cancellationToken = default)
    {
        if (!ImageFormat.IsStoredFileName(fileName))
        {
            return null;
        }

        var access = await ResolveAccessAsync(noteId, cancellationToken);
        if (access.Level == NoteAccessLevel.Read)
        {
            // A Read viewer only gets images the note itself mentions. Owner and Edit skip this: an Edit recipient
            // can put any name into the body anyway, and skipping it avoids a broken image right after a paste,
            // before the debounced autosave has written the link.
            var raw = await _fileStore.ReadRawAsync(access.OwnerUserId, noteId, cancellationToken);
            if (!ImageFormat.FindStoredFileNames(raw).Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return await _imageStore.OpenReadAsync(access.OwnerUserId, fileName, cancellationToken);
    }

    public async Task<string> SaveImageAsync(Guid noteId, Stream content, CancellationToken cancellationToken = default)
    {
        var access = await ResolveAccessAsync(noteId, cancellationToken);
        RequireBodyEdit(access, noteId);

        // The owner's folder, even for an Edit recipient: the owner must be able to see the image, and the owner's
        // orphan cleanup only looks at the owner's folder and notes.
        return await _imageStore.SaveAsync(access.OwnerUserId, content, cancellationToken);
    }

    /// <summary>
    /// Who the current user is to <paramref name="noteId"/>, re-resolved on every call (nothing cached, so an unshare
    /// or downgrade takes effect on the recipient's next action): the owner if the live file is in their own folder,
    /// else a recipient if a share row exists, else <see cref="NoteNotFoundException"/>.
    /// </summary>
    private async Task<NoteAccess> ResolveAccessAsync(Guid noteId, CancellationToken cancellationToken)
    {
        var me = _currentUser.UserId;

        // Own-first: an owner's notes cost a stat, no database query.
        if (await _fileStore.ExistsAsync(me, noteId, cancellationToken))
        {
            return new NoteAccess(NoteAccessLevel.Owner, me, null);
        }

        if (await _shares.FindAsync(noteId, me, cancellationToken) is { } grant)
        {
            var level = grant.Permission == NoteSharePermission.Edit ? NoteAccessLevel.Edit : NoteAccessLevel.Read;
            return new NoteAccess(level, grant.OwnerUserId, grant.OwnerDisplayName);
        }

        throw new NoteNotFoundException(noteId);
    }

    private static void RequireBodyEdit(NoteAccess access, Guid noteId)
    {
        if (!access.CanEditBody)
        {
            throw new NoteAccessDeniedException(noteId, "This note is shared with you read-only.");
        }
    }

    /// <summary>
    /// The write path for an Edit recipient: a compare-and-swap merge on the owner's file. Reads the raw file,
    /// applies <paramref name="apply"/> to the freshly deserialized note (so Title/Tags/Type/CreatedAt are the
    /// file's own, and the owner's concurrent rename or the auto-tagger's tags survive), stamps UpdatedAt and
    /// swaps it in only if the file is unchanged, retrying a few times. Never creates a file, so a trashed or
    /// purged note stays gone.
    /// </summary>
    /// <exception cref="NoteNotFoundException">The owner's live file no longer exists.</exception>
    /// <exception cref="IOException">The file kept changing (or was locked) across every attempt.</exception>
    private async Task<Note> ReplaceBodyAsync(Guid ownerId, Guid noteId, Action<Note> apply, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxReplaceAttempts; attempt++)
        {
            var raw = await _fileStore.ReadRawAsync(ownerId, noteId, cancellationToken);
            var current = _serializer.Deserialize(raw);
            apply(current);
            current.UpdatedAt = DateTimeOffset.UtcNow;

            if (await _fileStore.ReplaceIfUnchangedAsync(ownerId, noteId, raw, current, cancellationToken))
            {
                return current;
            }
        }

        throw new IOException("The note changed while saving — try again.");
    }

    private async Task WriteAndIndexAsync(Guid ownerId, Note note, CancellationToken cancellationToken)
    {
        // File first, then index: a crash in between leaves a file the next reconciliation will
        // discover and index, never an index entry pointing at a file that was never written.
        await _fileStore.WriteAsync(ownerId, note, cancellationToken);
        var writeTimeUtc = await _fileStore.GetLastWriteTimeUtcAsync(ownerId, note.Id, cancellationToken);
        await IndexAsync(ownerId, note, writeTimeUtc, cancellationToken);
    }

    /// <summary>
    /// Brings the owner's index row up to date after <paramref name="note"/> was written to the file:
    /// everything for <see cref="NoteSaveScope.Metadata"/>, just Body/UpdatedAt for <see cref="NoteSaveScope.ContentOnly"/>.
    /// </summary>
    private async Task IndexSavedAsync(Guid ownerId, Note note, NoteSaveScope scope, CancellationToken cancellationToken)
    {
        if (scope == NoteSaveScope.Metadata)
        {
            var writeTimeUtc = await _fileStore.GetLastWriteTimeUtcAsync(ownerId, note.Id, cancellationToken);
            await IndexAsync(ownerId, note, writeTimeUtc, cancellationToken);
        }
        else
        {
            await _indexRepository.UpdateContentAsync(ownerId, note.Id, note.Body, note.UpdatedAt, cancellationToken);
        }
    }

    private async Task IndexAsync(Guid ownerId, Note note, DateTimeOffset writeTimeUtc, CancellationToken cancellationToken)
    {
        var entry = new NoteIndexEntry
        {
            Id = note.Id,
            UserId = ownerId,
            Title = note.Title,
            Body = note.Body,
            NoteType = note.Type.ToString(),
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
            IndexedFileWriteTimeUtc = writeTimeUtc,
            // Relative to the notes folder, not absolute: NoteService doesn't know the folder
            // setting, and an absolute path would go stale if it's ever changed.
            FilePath = $"{ownerId}/{note.Id}.md",
        };

        await _indexRepository.UpsertAsync(entry, note.Tags, cancellationToken);
    }
}
