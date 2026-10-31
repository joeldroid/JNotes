using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class DraftGuardTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Save_without_title_shows_error()
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/notes/new");
            await s.Page.GetByLabel("Note body").FillAsync("a body but no title");
            await s.Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            await Expect(s.Page.GetByText("Give the note a title before saving.")).ToBeVisibleAsync();
            await Expect(s.Page).ToHaveURLAsync(new Regex("/notes/new$"));
        }, s);
    }

    [UiFact]
    public async Task New_Note_on_a_dirty_draft_prompts_and_Keep_editing_keeps_content()
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/notes/new");
            var body = s.Page.GetByLabel("Note body");
            await body.FillAsync("precious draft text");

            await Ui.NewNoteButton(s.Page).ClickAsync();
            await Expect(s.Page.GetByText("Discard this new note?")).ToBeVisibleAsync();
            await Ui.ConfirmDialogAsync(s.Page, "Keep editing");
            await Expect(s.Page.GetByText("Discard this new note?")).ToBeHiddenAsync();
            await Expect(body).ToHaveValueAsync("precious draft text");
            await Expect(s.Page).ToHaveURLAsync(new Regex("/notes/new$"));
        }, s);
    }

    [UiFact]
    public async Task New_Note_then_Discard_gives_an_empty_draft()
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/notes/new");
            var tag = "throwaway-" + Ui.Token();
            await s.Page.GetByPlaceholder("Untitled note").FillAsync("Throw-away title");
            await s.Page.GetByPlaceholder("Add a tag…").FillAsync(tag);
            await s.Page.GetByText($"Add new tag “{tag}”").ClickAsync();
            await Expect(s.Page.Locator(".jn-tag-input .mud-chip")).ToHaveCountAsync(1);
            await s.Page.GetByLabel("Note body").FillAsync("throw-away draft text");

            await Ui.NewNoteButton(s.Page).ClickAsync();
            await Expect(s.Page.GetByText("Discard this new note?")).ToBeVisibleAsync();
            await Ui.ConfirmDialogAsync(s.Page, "Discard");
            await Expect(s.Page.GetByText("Discard this new note?")).ToBeHiddenAsync();

            // The same draft page, visibly reset: title, tags and body are all empty.
            await Expect(s.Page).ToHaveURLAsync(new Regex("/notes/new$"));
            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("");
            await Expect(s.Page.GetByPlaceholder("Untitled note")).ToHaveValueAsync("");
            await Expect(s.Page.Locator(".jn-tag-input .mud-chip")).ToHaveCountAsync(0);
            await Expect(s.Page.GetByText("Not saved yet")).ToBeVisibleAsync();

            // The reset draft behaves like any new one: nothing left to lose, so New Note no longer prompts.
            await Ui.NewNoteButton(s.Page).ClickAsync();
            await Expect(s.Page.GetByText("Discard this new note?")).ToHaveCountAsync(0);
            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("");
        }, s);
    }

    [UiFact]
    public async Task New_Note_on_an_empty_draft_does_not_prompt()
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/notes/new");
            await Ui.NewNoteButton(s.Page).ClickAsync();
            await Expect(s.Page.GetByText("Discard this new note?")).ToHaveCountAsync(0);
            await Expect(s.Page).ToHaveURLAsync(new Regex("/notes/new$"));
            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("");
            await Expect(s.Page.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeVisibleAsync();
        }, s);
    }
}
