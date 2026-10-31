using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class TrashTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Delete_restore_and_delete_forever()
    {
        var title = Ui.Unique("Trash me");
        var id = await app.SeedNoteAsync(app.Admin, title, "to be trashed");
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await TrashFromViewAsync(s, id);

            // In Trash, with no chips (the note has no tags and the type chip is gone).
            await s.GotoInteractiveAsync("/trash");
            var row = s.Page.Locator(".jn-trash-item").Filter(new() { HasText = title });
            await Expect(row).ToHaveCountAsync(1);
            await Expect(row).ToContainTextAsync("Deleted ");
            await Expect(row.Locator(".mud-chip")).ToHaveCountAsync(0);
            Assert.False(File.Exists(app.NoteFilePath(app.Admin, id)));
            Assert.True(File.Exists(app.TrashedNoteFilePath(app.Admin, id)));

            // Restore: gone from Trash, back in All Notes.
            await row.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
            await Expect(row).ToHaveCountAsync(0);
            await s.Page.GetByRole(AriaRole.Link, new() { Name = "All Notes" }).ClickAsync();
            await Expect(s.Page.Locator(".jn-note-item").Filter(new() { HasText = title })).ToHaveCountAsync(1);
            Assert.True(File.Exists(app.NoteFilePath(app.Admin, id)));

            // Trash it again and delete it forever.
            await TrashFromViewAsync(s, id);
            await s.Page.GetByRole(AriaRole.Link, new() { Name = "Trash", Exact = true }).ClickAsync();
            row = s.Page.Locator(".jn-trash-item").Filter(new() { HasText = title });
            await row.GetByRole(AriaRole.Button, new() { Name = "Delete forever" }).ClickAsync();
            await Ui.ConfirmDialogAsync(s.Page, "Delete forever");
            await Expect(row).ToHaveCountAsync(0);
            Assert.False(File.Exists(app.NoteFilePath(app.Admin, id)));
            Assert.False(File.Exists(app.TrashedNoteFilePath(app.Admin, id)));
        }, s);
    }

    private static async Task TrashFromViewAsync(UiSession s, Guid id)
    {
        await s.GotoInteractiveAsync($"/notes/{id}");
        await s.Page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Ui.ConfirmDialogAsync(s.Page, "Delete");
        await s.Page.WaitForURLAsync(new Regex(@"^[^?#]*/$"));
    }
}
