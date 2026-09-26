namespace JNotes.Core.Export;

public sealed record NotePdf(string FileName, byte[] Content);

/// <summary>
/// Implicit current user (via <c>INoteService</c>/<c>IImageService</c>), like <c>NoteService</c> — a
/// page/endpoint can't export another user's note.
/// </summary>
public interface INoteExportService
{
    /// <exception cref="Storage.NoteNotFoundException">No such note for the current user.</exception>
    /// <exception cref="PdfRenderingException">The PDF service failed.</exception>
    Task<NotePdf> ExportPdfAsync(Guid noteId, CancellationToken cancellationToken = default);
}
