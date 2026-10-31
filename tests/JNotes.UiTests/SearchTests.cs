using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class SearchTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Search_finds_by_body_text()
    {
        var suffix = Ui.Token();
        var title = Ui.Unique("Body search");
        await app.SeedNoteAsync(app.Admin, title, $"nothing special except zebra-{suffix} here");
        await SearchAndExpectAsync($"zebra-{suffix}", title);
    }

    [UiFact]
    public async Task Search_finds_by_tag()
    {
        var suffix = Ui.Token();
        var title = Ui.Unique("Tag search");
        await app.SeedNoteAsync(app.Admin, title, "a body without the token", $"tag-{suffix}");
        await SearchAndExpectAsync($"tag-{suffix}", title);
    }

    private async Task SearchAndExpectAsync(string term, string expectedTitle)
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/");
            var search = s.Page.GetByPlaceholder("Search", new() { Exact = true });
            await search.ClickAsync();
            await search.FillAsync(term);
            await search.PressAsync("Enter");

            await s.Page.WaitForURLAsync(new Regex(@"/\?q=" + Regex.Escape(term) + "$"));
            await Expect(s.Page.GetByText("Search results for")).ToBeVisibleAsync();
            var hits = s.Page.Locator(".jn-note-item");
            await Expect(hits).ToHaveCountAsync(1);
            await Expect(hits.First).ToContainTextAsync(expectedTitle);
        }, s);
    }
}
