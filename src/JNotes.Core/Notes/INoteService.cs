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

    /// <exception cref="InvalidOperationException">The note is not a <see cref="NoteType.Checklist"/> note, or the line isn't a checklist item.</exception>
    Task<Note> ToggleChecklistItemAsync(Guid noteId, int lineIndex, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
