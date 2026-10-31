using JNotes.Core.Indexing;
using JNotes.UiTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class LegacyChecklistTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Legacy_checklist_file_opens_as_text_and_is_rewritten_as_text()
    {
        var id = Guid.NewGuid();
        var title = Ui.Unique("Old list");
        var folder = Path.Combine(app.NotesPath, app.Admin.Id.ToString());
        Directory.CreateDirectory(folder);
        var legacy = $"---\nid: {id}\ntitle: {title}\ntype: checklist\ntags: []\n"
            + "created: 2026-01-02T03:04:05.0000000Z\nupdated: 2026-01-02T03:04:05.0000000Z\n---\n- [ ] a\n- [x] b";
        await File.WriteAllTextAsync(app.NoteFilePath(app.Admin, id), legacy);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<INoteIndexReconciler>().RescanAsync(app.Admin.Id);
            Assert.Empty(result.Errors);
            Assert.True(result.Added >= 1, "The legacy file was not indexed.");
        }

        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/");
            var item = s.Page.Locator(".jn-note-item").Filter(new() { HasText = title });
            await Expect(item).ToHaveCountAsync(1);
            await item.Locator("a.jn-note-link").ClickAsync();
            await s.Page.WaitForURLAsync($"**/notes/{id}");

            // Opens as ordinary markdown: inert task-list boxes, no type chip.
            var boxes = s.Page.Locator(".note-view .markdown-preview .task-list-item input[type=checkbox]");
            await Expect(boxes).ToHaveCountAsync(2);
            await Expect(boxes.Nth(0)).ToBeDisabledAsync();
            await Expect(boxes.Nth(0)).Not.ToBeCheckedAsync();
            await Expect(boxes.Nth(1)).ToBeCheckedAsync();
            await Expect(s.Page.Locator(".jn-meta .mud-chip")).ToHaveCountAsync(0);

            await s.Page.GetByRole(AriaRole.Link, new() { Name = "Edit" }).ClickAsync();
            var body = s.Page.GetByLabel("Note body");
            await Expect(body).ToHaveValueAsync("- [ ] a\n- [x] b");
            await body.FillAsync("- [ ] a\n- [x] b c");
            await Ui.WaitForAutosaveAsync(s.Page);

            var file = await app.ReadNoteFileEventuallyAsync(app.Admin, id, text => text.Contains("type: text", StringComparison.Ordinal));
            Assert.DoesNotContain("checklist", file);
            Assert.Contains("- [ ] a", file);
            Assert.Contains("- [x] b c", file);
        }, s);
    }
}
