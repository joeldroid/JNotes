using JNotes.Core.Export;
using JNotes.Core.Images;
using JNotes.Core.Models;
using JNotes.Core.Storage;
using JNotes.Tests.Images;
using JNotes.Tests.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Integration;

public class NoteExportServiceTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private IImageService Images => _fixture.Scope.ServiceProvider.GetRequiredService<IImageService>();

    [Fact]
    public async Task Exporting_a_text_note_renders_it_and_returns_the_bytes()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Q3 / Plan", ["work"], "hello **world**");

        var pdf = await _fixture.NoteExportService.ExportPdfAsync(note.Id);

        Assert.Equal(1, _fixture.PdfRenderer.CallCount);
        Assert.Contains("Q3 / Plan", _fixture.PdfRenderer.LastRequest!.IndexHtml);
        Assert.Equal("Q3 Plan.pdf", pdf.FileName);
        Assert.Equal(RecordingPdfRenderer.Output, pdf.Content);
        foreach (var font in PdfExportAssets.Fonts)
        {
            Assert.Contains(PdfExportAssets.FileNameOf(font), _fixture.PdfRenderer.AssetBytes.Keys);
        }

        Assert.DoesNotContain(NotePdfDocumentBuilder.MermaidScriptFileName, _fixture.PdfRenderer.AssetBytes.Keys);
    }

    [Fact]
    public async Task A_referenced_stored_image_is_attached_and_a_missing_one_is_skipped()
    {
        var bytes = new byte[64];
        ImageFormatTests.Png.CopyTo(bytes, 0);
        var stored = await Images.SaveAsync(new MemoryStream(bytes));
        const string missing = "0123456789abcdef0123456789abcdef.png";
        var note = await _fixture.NoteService.CreateAsync(
            NoteType.Text, "Pics", [], $"![a](images/{stored})\n\n![b](images/{missing})");

        await _fixture.NoteExportService.ExportPdfAsync(note.Id);

        Assert.Equal(64, _fixture.PdfRenderer.AssetBytes[stored].Length);
        Assert.DoesNotContain(missing, _fixture.PdfRenderer.AssetBytes.Keys);
    }

    [Fact]
    public async Task A_mermaid_note_attaches_the_mermaid_script()
    {
        var note = await _fixture.NoteService.CreateAsync(
            NoteType.Text, "Diagram", [], "```mermaid\nflowchart LR\n  A --> B\n```");

        await _fixture.NoteExportService.ExportPdfAsync(note.Id);

        Assert.Contains(NotePdfDocumentBuilder.MermaidScriptFileName, _fixture.PdfRenderer.AssetBytes.Keys);
    }

    [Fact]
    public async Task A_checklist_note_renders_its_items()
    {
        var note = await _fixture.NoteService.CreateAsync(NoteType.Checklist, "Todo", [], "- [ ] one\n- [x] two");

        await _fixture.NoteExportService.ExportPdfAsync(note.Id);

        var html = _fixture.PdfRenderer.LastRequest!.IndexHtml;
        Assert.Contains("<span class=\"text\">one</span>", html);
        Assert.Contains("<li class=\"checked\"><span class=\"box\"></span><span class=\"text\">two</span>", html);
    }

    [Fact]
    public async Task Another_users_note_is_not_found_and_nothing_is_rendered()
    {
        _fixture.ActAsUserA();
        var note = await _fixture.NoteService.CreateAsync(NoteType.Text, "Private", [], "secret");

        _fixture.ActAsUserB();

        await Assert.ThrowsAsync<NoteNotFoundException>(() => _fixture.NoteExportService.ExportPdfAsync(note.Id));
        Assert.Equal(0, _fixture.PdfRenderer.CallCount);
    }

    [Fact]
    public async Task A_missing_note_is_not_found()
    {
        await Assert.ThrowsAsync<NoteNotFoundException>(() => _fixture.NoteExportService.ExportPdfAsync(Guid.NewGuid()));
    }
}
