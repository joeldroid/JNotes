using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class SharingTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Edit_share_lets_bob_edit_then_View_only_removes_Edit()
    {
        var title = Ui.Unique("Shared");
        var id = await app.SeedNoteAsync(app.Admin, title, "owner text");
        await using var admin = await UiSession.StartAsync(app, app.Admin, output);
        await using var bob = await UiSession.StartAsync(app, app.Bob, output);
        await UiSession.RunAsync(async () =>
        {
            // Admin shares with bob as "Can edit".
            await admin.GotoInteractiveAsync($"/notes/{id}");
            await admin.Page.GetByRole(AriaRole.Button, new() { Name = "Share", Exact = true }).ClickAsync();
            var dialog = admin.Page.Locator(".mud-dialog");
            await Expect(dialog).ToContainTextAsync("they can also change its text.");

            await dialog.GetByPlaceholder("Search people…").FillAsync("bob");
            await PopoverOption(admin.Page, "bob (bob)").ClickAsync();
            await ChoosePermissionAsync(admin.Page, dialog.GetByRole(AriaRole.Combobox, new() { Name = "Permission for the new person" }), "Can edit");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
            await Expect(dialog.Locator(".jn-share-row")).ToHaveCountAsync(1);
            await Expect(dialog.Locator(".jn-share-row")).ToContainTextAsync("bob");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
            await Expect(admin.Page.GetByRole(AriaRole.Button, new() { Name = "Shared with 1" })).ToBeVisibleAsync();

            // Bob finds it under Shared with Me and edits the body.
            await bob.GotoInteractiveAsync("/shared");
            var item = bob.Page.Locator(".jn-note-item").Filter(new() { HasText = title });
            await Expect(item).ToHaveCountAsync(1);
            await Expect(item.Locator(".jn-share-chip")).ToContainTextAsync("Shared by admin · Can edit");
            await item.Locator("a.jn-note-link").ClickAsync();
            await bob.Page.WaitForURLAsync(new Regex($"/notes/{id}$"));
            await bob.Page.GetByRole(AriaRole.Link, new() { Name = "Edit" }).ClickAsync();

            var marker = "bob wrote " + Ui.Token();
            var body = bob.Page.GetByLabel("Note body");
            await Expect(body).ToHaveValueAsync("owner text");
            await body.FillAsync("owner text\n\n" + marker);
            await Ui.WaitForAutosaveAsync(bob.Page);

            // Admin sees the edit.
            await admin.GotoInteractiveAsync($"/notes/{id}");
            await Expect(admin.Page.Locator(".markdown-preview")).ToContainTextAsync(marker);

            // Admin downgrades bob to View only.
            await admin.Page.GetByRole(AriaRole.Button, new() { Name = "Shared with 1" }).ClickAsync();
            await ChoosePermissionAsync(admin.Page, dialog.GetByRole(AriaRole.Combobox, new() { Name = "Permission for bob" }), "View only");
            await Expect(dialog.GetByRole(AriaRole.Combobox, new() { Name = "Permission for bob" })).ToContainTextAsync("View only");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

            // Bob's next load of the note has no Edit and says View only.
            await bob.GotoInteractiveAsync($"/notes/{id}");
            await Expect(bob.Page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync();
            await Expect(bob.Page.GetByRole(AriaRole.Link, new() { Name = "Edit" })).ToHaveCountAsync(0);
            await Expect(bob.Page.Locator(".jn-meta")).ToContainTextAsync("View only");
        }, admin, bob);
    }

    // A MudBlazor popover item (autocomplete suggestion or select option) in whichever popover is open.
    private static ILocator PopoverOption(IPage page, string text) =>
        page.Locator(".mud-popover-open").GetByText(text, new() { Exact = true });

    private static async Task ChoosePermissionAsync(IPage page, ILocator select, string option)
    {
        await select.ClickAsync();
        var item = PopoverOption(page, option);
        await Expect(item).ToBeVisibleAsync();
        await item.ClickAsync();
    }
}
