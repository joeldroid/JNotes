using JNotes.Core.Export;
using JNotes.Core.Storage;
using Microsoft.Net.Http.Headers;

namespace JNotes.Web.Export;

internal static class NotePdfEndpoints
{
    // An endpoint (not an @onclick) because only a real HTTP response can hand the browser a file;
    // the button's anchor has target="_blank", so Blazor's router doesn't intercept it and the browser
    // does a plain GET with the auth cookie. Runs in an ordinary request scope, where HttpContext
    // always exists, so the ICurrentUserContext gotcha doesn't apply.
    public static IEndpointRouteBuilder MapNotePdfExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/notes/{id:guid}/pdf", async (Guid id, INoteExportService export, HttpContext context, CancellationToken cancellationToken) =>
        {
            NotePdf pdf;
            try
            {
                pdf = await export.ExportPdfAsync(id, cancellationToken);
            }
            catch (NoteNotFoundException)
            {
                // Another user's note lands here too — rendered by the /not-found status page.
                return Results.NotFound();
            }
            catch (PdfRenderingException)
            {
                // Has a body, so UseStatusCodePagesWithReExecute doesn't replace it with "Not found".
                // The details were already logged by the renderer.
                return Results.Content(
                    UnavailablePage(id), "text/html; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(pdf.FileName);
            context.Response.Headers.ContentDisposition = disposition.ToString();
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(pdf.Content, "application/pdf");
        }).RequireAuthorization();

        return app;
    }

    private static string UnavailablePage(Guid id) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>Couldn't create the PDF</title>
        <style>body { font-family: sans-serif; max-width: 32rem; margin: 4rem auto; padding: 0 1rem; color: #424242; line-height: 1.5; } a { color: #2f6feb; }</style>
        </head><body>
        <h1>Couldn't create the PDF</h1>
        <p>The PDF service isn't reachable right now. Close this tab and try again in a moment — if it keeps happening, ask your administrator to check the Gotenberg container.</p>
        <p><a href="/notes/{{id}}">Back to the note</a></p>
        </body></html>
        """;
}
