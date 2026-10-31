using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class NewNoteButtonTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task New_Note_is_a_single_button_that_opens_an_empty_draft()
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/");
            var newNote = Ui.NewNoteButton(s.Page);
            await Expect(newNote).ToHaveCountAsync(1);

            // No dropdown any more: not a menu activator, and no menu or trailing dropdown icon inside the container.
            await Expect(newNote).Not.ToHaveAttributeAsync("aria-haspopup", new Regex(".*"));
            await Expect(s.Page.Locator(".jn-nav-new .mud-menu, .jn-nav-new .mud-button-icon-end")).ToHaveCountAsync(0);

            await newNote.ClickAsync();
            await s.Page.WaitForURLAsync(new Regex("/notes/new$"));
            await Expect(s.Page.GetByPlaceholder("Untitled note")).ToHaveValueAsync("");
            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("");
            await Expect(s.Page.GetByText("Not saved yet")).ToBeVisibleAsync();
            await Expect(s.Page.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeVisibleAsync();
        }, s);
    }

    [UiTheory]
    [InlineData("/notes/new/text")]
    [InlineData("/notes/new/checklist")]
    public async Task Old_typed_new_note_routes_show_not_found(string path)
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        s.Allow(@"http 404: GET .*/notes/new/"); // the document itself is a 404
        s.Allow(@"console\.error: Failed to load resource: .*404");
        await UiSession.RunAsync(async () =>
        {
            var response = await s.Page.GotoAsync(path);
            Assert.Equal(404, response!.Status);
            await Expect(s.Page.GetByRole(AriaRole.Heading, new() { Name = "Not Found", Level = 1 })).ToBeVisibleAsync();
        }, s);
    }
}
