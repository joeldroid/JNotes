using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class PdfExportTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiGotenbergFact]
    public async Task Export_link_returns_a_pdf()
    {
        var title = Ui.Unique("Export me");
        var id = await app.SeedNoteAsync(app.Admin, title, "# Heading\n\n- [ ] one\n- [x] two\n\nSome **bold** text.");
        await using var admin = await UiSession.StartAsync(app, app.Admin, output);
        await using var bob = await UiSession.StartAsync(app, app.Bob, output);
        bob.Allow($@"http 404: GET .*/notes/{id}/pdf");
        bob.Allow(@"console\.error: Failed to load resource: .*404");
        await UiSession.RunAsync(async () =>
        {
            await admin.GotoInteractiveAsync($"/notes/{id}");
            var link = admin.Page.GetByRole(AriaRole.Link, new() { Name = "Export PDF" });
            await Expect(link).ToHaveAttributeAsync("href", $"/notes/{id}/pdf");
            await Expect(link).ToHaveAttributeAsync("target", "_blank");

            var response = await admin.Context.APIRequest.GetAsync($"{app.BaseUrl}notes/{id}/pdf", new() { Timeout = 60_000 });
            Assert.Equal(200, response.Status);
            Assert.StartsWith("application/pdf", response.Headers["content-type"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("inline", response.Headers["content-disposition"], StringComparison.OrdinalIgnoreCase);
            var bytes = await response.BodyAsync();
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

            // Bob was given nothing: the same URL is a 404 for him.
            var denied = await bob.Context.APIRequest.GetAsync($"{app.BaseUrl}notes/{id}/pdf");
            Assert.Equal(404, denied.Status);
        }, admin, bob);
    }
}
