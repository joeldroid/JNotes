using JNotes.Core.Models;

namespace JNotes.Core.Notes;

/// <summary>
/// The note operations Blazor components use. This — together with <c>ISettingsService</c> and
/// <c>INoteIndexReconciler</c> — is the entire surface area the Web project consumes from Core;
/// Web never touches <c>INoteFileStore</c>, EF Core, or the filesystem directly.
/// </summary>
public interface INoteService
{
    /// <summary>Fast, index-only search — never reads note files.</summary>
    Task<IReadOnlyList<NoteSummary>> ListAsync(NoteSearchQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetDistinctTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the full note, including body, from its file.</summary>
    Task<Note> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Note> CreateAsync(NoteType type, string title, IEnumerable<string> tags, string body, CancellationToken cancellationToken = default);

    /// <summary>Full flush — writes the file and updates the index. Equivalent to <c>AutosaveAsync(note, NoteSaveScope.Metadata)</c>.</summary>
    Task<Note> UpdateAsync(Note note, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="note"/> at whatever scope actually changed — see
    /// <see cref="NoteSaveScope"/>. Always bumps <see cref="Note.UpdatedAt"/>: even a
    /// <see cref="NoteSaveScope.ContentOnly"/> save is a real edit, so the file's own timestamp
    /// should reflect it — the index just doesn't find out about it until the next thing that
    /// does touch the index (a <see cref="NoteSaveScope.Metadata"/> save, or reconciliation).
    /// </summary>
    Task<Note> AutosaveAsync(Note note, NoteSaveScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets one checklist item's checked state and re-sorts its block so checked items sit after
    /// unchecked ones. Idempotent: setting an item to the state it is already in only re-sorts.
    /// </summary>
    /// <exception cref="InvalidOperationException">The note is not a <see cref="NoteType.Checklist"/> note, or the line isn't a checklist item.</exception>
    Task<Note> SetChecklistItemCheckedAsync(Guid noteId, int lineIndex, bool isChecked, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends one unchecked item per non-blank line of <paramref name="text"/> to the note's last
    /// checklist block, above any checked items in it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The note is not a <see cref="NoteType.Checklist"/> note.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> has no non-blank content.</exception>
    Task<Note> AddChecklistItemAsync(Guid noteId, string text, CancellationToken cancellationToken = default);

    /// <summary>Moves the note to the trash (file move + index removal) rather than deleting it outright — see <see cref="PurgeAsync"/> for that.</summary>
    Task TrashAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every note currently in the trash, newest-trashed first. Reads the trash folder directly — there's no SQLite index for it.</summary>
    Task<IReadOnlyList<TrashedNoteSummary>> ListTrashAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves a trashed note back out of the trash and re-adds it to the index.</summary>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">The note isn't in the trash.</exception>
    Task<Note> RestoreAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes a note from the trash. Only reachable for notes already trashed — there's no way to purge a live note directly.</summary>
    Task PurgeAsync(Guid id, CancellationToken cancellationToken = default);
}
