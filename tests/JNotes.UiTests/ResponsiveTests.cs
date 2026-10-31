using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class ResponsiveTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Drawer_closes_after_New_Note_on_a_narrow_viewport()
    {
        // 800 px is MudBlazor's Sm breakpoint, below the Md (960 px) at which the drawer stops being an overlay.
        await using var s = await UiSession.StartAsync(app, app.Admin, output, new ViewportSize { Width = 800, Height = 1000 });
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync("/");
            var drawer = s.Page.Locator(".mud-drawer").First;
            var closed = new Regex("mud-drawer--closed");
            if (await HasClassAsync(drawer, closed))
            {
                await s.Page.GetByRole(AriaRole.Button, new() { Name = "Toggle navigation" }).ClickAsync();
            }

            await Expect(drawer).Not.ToHaveClassAsync(closed, new() { Timeout = 10_000 });

            await Ui.NewNoteButton(s.Page).ClickAsync();
            await s.Page.WaitForURLAsync(new Regex("/notes/new$"));
            await Expect(drawer).ToHaveClassAsync(closed, new() { Timeout = 10_000 });
        }, s);
    }

    private static async Task<bool> HasClassAsync(ILocator element, Regex pattern) =>
        pattern.IsMatch(await element.GetAttributeAsync("class") ?? "");
}
