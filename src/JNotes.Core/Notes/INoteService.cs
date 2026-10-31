using JNotes.Core.Models;
using JNotes.Core.Sharing;
using JNotes.Core.Storage;

namespace JNotes.Core.Notes;

/// <summary>
/// The note operations Blazor components use. This — together with <c>ISettingsService</c> and
/// <c>INoteIndexReconciler</c> — is the entire surface area the Web project consumes from Core;
/// Web never touches <c>INoteFileStore</c>, EF Core, or the filesystem directly.
/// </summary>
public interface INoteService
{
    /// <summary>
    /// Fast, index-only search — never reads note files. Honours <see cref="NoteSearchQuery.Limit"/>, SQL-side.
    /// </summary>
    Task<IReadOnlyList<NoteSummary>> ListAsync(NoteSearchQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetDistinctTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>The current user's previously used tags matching <paramref name="term"/> (case-insensitive; prefix matches first; empty term = most used), excluding <paramref name="exclude"/> case-insensitively. Index-only.</summary>
    Task<IReadOnlyList<string>> SuggestTagsAsync(string? term, IReadOnlyCollection<string> exclude, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the full note, including body, from its file — the current user's own note, or one another user has
    /// shared with them (read from the owner's folder).
    /// </summary>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">The note doesn't exist, or the current user neither owns it nor has been given access.</exception>
    Task<Note> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="GetAsync"/>, plus how the current user relates to the note (owner, or Edit/Read recipient).</summary>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">No access to the note.</exception>
    Task<NoteWithAccess> GetWithAccessAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Note> CreateAsync(string title, IEnumerable<string> tags, string body, CancellationToken cancellationToken = default);

    /// <summary>Full flush — writes the file and updates the index. Equivalent to <c>AutosaveAsync(note, NoteSaveScope.Metadata)</c>.</summary>
    Task<Note> UpdateAsync(Note note, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="note"/> at whatever scope actually changed — see
    /// <see cref="NoteSaveScope"/>. Always bumps <see cref="Note.UpdatedAt"/>: even a
    /// <see cref="NoteSaveScope.ContentOnly"/> save is a real edit, so the file's timestamp and the
    /// index row's Body/UpdatedAt both reflect it (only a <see cref="NoteSaveScope.Metadata"/> save
    /// also rewrites the title and tag rows).
    /// Works on the current user's own note or one shared with them at Edit permission. An Edit recipient's save
    /// persists only <see cref="Note.Body"/> (Title/Tags come from the file; the returned note carries them), and
    /// never creates a file.
    /// </summary>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">No live note file for the caller to save to (never created, trashed, purged, or access revoked).</exception>
    /// <exception cref="NoteAccessDeniedException">The caller has Read-only access.</exception>
    /// <exception cref="IOException">An Edit recipient's write kept colliding with another writer.</exception>
    Task<Note> AutosaveAsync(Note note, NoteSaveScope scope, CancellationToken cancellationToken = default);

    /// <summary>Moves the note to the trash (file move + index removal) rather than deleting it outright — see <see cref="PurgeAsync"/> for that.</summary>
    Task TrashAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every note currently in the trash, newest-trashed first. Reads the trash folder directly — there's no index for it.</summary>
    Task<IReadOnlyList<TrashedNoteSummary>> ListTrashAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves a trashed note back out of the trash and re-adds it to the index.</summary>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">The note isn't in the trash.</exception>
    Task<Note> RestoreAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes a note from the trash. Only reachable for notes already trashed — there's no way to purge a live note directly.</summary>
    Task PurgeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an image referenced by <paramref name="noteId"/> from the note owner's folder. <c>null</c> for an invalid
    /// name, a missing file, or — for a Read recipient — an image the note's text doesn't mention.
    /// </summary>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">No access to the note.</exception>
    Task<ImageContent?> OpenImageAsync(Guid noteId, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Stores an image for <paramref name="noteId"/> in the note <b>owner's</b> folder and returns its stored name.</summary>
    /// <exception cref="JNotes.Core.Storage.NoteNotFoundException">No access to the note.</exception>
    /// <exception cref="NoteAccessDeniedException">The caller has Read-only access.</exception>
    Task<string> SaveImageAsync(Guid noteId, Stream content, CancellationToken cancellationToken = default);
}
