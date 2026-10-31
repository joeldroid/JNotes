using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class CreateNoteTests(UiAppFixture app, ITestOutputHelper output)
{
    // A 1x1 transparent PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [UiFact]
    public async Task Create_view_and_list_show_no_type_chip_and_read_only_task_lists()
    {
        var suffix = Ui.Token();
        var title = $"Groceries {suffix}";
        var tag = $"shop-{suffix}";
        const string Body = "- [ ] milk\n- [x] eggs\n\n```mermaid\nflowchart LR\n  A-->B\n```\n";

        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/notes/new");
            await s.Page.GetByPlaceholder("Untitled note").FillAsync(title);

            var tagInput = s.Page.GetByPlaceholder("Add a tag…");
            await tagInput.FillAsync(tag);
            await s.Page.GetByText($"Add new tag “{tag}”").ClickAsync();
            await Expect(s.Page.Locator(".jn-tag-input .mud-chip")).ToContainTextAsync(tag);

            await s.Page.GetByLabel("Note body").FillAsync(Body);

            // The editor's preview renders task lists as inert boxes without bullets.
            await ExpectTaskListAsync(s.Page, ".note-editor-preview");
            await Expect(s.Page.Locator(".note-editor-preview pre.mermaid svg")).ToBeVisibleAsync(new() { Timeout = 20_000 });

            await s.Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await s.Page.WaitForURLAsync(new Regex("/notes/[0-9a-f-]{36}$"));
            var id = Ui.NoteIdFromUrl(s.Page.Url);

            // The view: no type chip, no "Checklist" anywhere, read-only boxes, rendered diagram.
            await Expect(s.Page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync();
            await Expect(s.Page.Locator(".jn-meta .mud-chip")).ToHaveCountAsync(0);
            await Expect(s.Page.GetByText("Checklist", new() { Exact = true })).ToHaveCountAsync(0);
            await ExpectTaskListAsync(s.Page, ".note-view .markdown-preview");
            var first = s.Page.Locator(".note-view .markdown-preview .task-list-item input[type=checkbox]").First;
            await first.ClickAsync(new() { Force = true });
            await Expect(first).Not.ToBeCheckedAsync();
            await Expect(s.Page.Locator(".markdown-preview pre.mermaid svg")).ToBeVisibleAsync(new() { Timeout = 20_000 });

            // The file on disk is an ordinary text note.
            var file = await app.ReadNoteFileEventuallyAsync(app.Admin, id, text => text.Contains("milk", StringComparison.Ordinal));
            Assert.Contains("type: text", file);

            // All Notes: exactly one chip, and it is the tag.
            await s.Page.GetByRole(AriaRole.Link, new() { Name = "All Notes" }).ClickAsync();
            var item = s.Page.Locator(".jn-note-item").Filter(new() { HasText = title });
            await Expect(item).ToHaveCountAsync(1);
            await Expect(item.Locator(".mud-chip")).ToHaveCountAsync(1);
            await Expect(item.Locator(".mud-chip")).ToHaveTextAsync(tag);

            // Recent: every entry carries the same (generic) icon, none a per-type one.
            var icons = s.Page.Locator(".jn-nav-recent-list .mud-nav-link svg");
            await Expect(icons.First).ToBeVisibleAsync();
            var markup = await icons.EvaluateAllAsync<string[]>("els => els.map(e => e.innerHTML)");
            Assert.NotEmpty(markup);
            Assert.Single(markup.Distinct());
        }, s);
    }

    [UiFact]
    public async Task Image_button_uploads_and_renders()
    {
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/notes/new");
            await s.Page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
            {
                Name = "pixel.png",
                MimeType = "image/png",
                Buffer = TinyPng,
            });

            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync(new Regex(@"\]\(images/[0-9a-f]{32}\.png\)"));
            var image = s.Page.Locator(".note-editor-preview img");
            await Expect(image).ToHaveCountAsync(1);
            await s.Page.WaitForFunctionAsync("() => { const i = document.querySelector('.note-editor-preview img'); return !!i && i.complete && i.naturalWidth > 0; }");
        }, s);
    }

    private static async Task ExpectTaskListAsync(IPage page, string scope)
    {
        var boxes = page.Locator($"{scope} .task-list-item input[type=checkbox]");
        await Expect(boxes).ToHaveCountAsync(2);
        await Expect(boxes.Nth(0)).ToBeDisabledAsync();
        await Expect(boxes.Nth(1)).ToBeDisabledAsync();
        await Expect(boxes.Nth(0)).Not.ToBeCheckedAsync();
        await Expect(boxes.Nth(1)).ToBeCheckedAsync();
        Assert.Equal("none", await Ui.ListStyleAsync(page.Locator($"{scope} ul.contains-task-list")));
    }
}
