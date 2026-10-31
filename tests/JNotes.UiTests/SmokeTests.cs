using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class SmokeTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Static_assets_and_login_page_are_served()
    {
        await using var s = await UiSession.StartAsync(app, Ui.Anonymous, output);
        await UiSession.RunAsync(async () =>
        {
            // Fails fast and clearly if the in-process host can't find Web's static web assets manifest.
            foreach (var asset in new[] { "/_framework/blazor.web.js", "/_content/MudBlazor/MudBlazor.min.css", "/css/app.css" })
            {
                var response = await s.Context.APIRequest.GetAsync(asset);
                Assert.True(response.Status == 200, $"GET {asset} returned {response.Status}.");
            }

            await s.Page.GotoAsync("/login");
            await Expect(s.Page.GetByText("Welcome to JNotes")).ToBeVisibleAsync();
        }, s);
    }

    [UiFact]
    public async Task Login_form_signs_in_and_rejects_a_wrong_password()
    {
        await using var s = await UiSession.StartAsync(app, Ui.Anonymous, output);
        await UiSession.RunAsync(async () =>
        {
            await s.Page.GotoAsync("/login");
            await s.Page.GetByLabel("Username").FillAsync(app.Admin.Username);
            await s.Page.GetByLabel("Password").FillAsync("definitely-not-the-password");
            await s.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
            await Expect(s.Page.GetByText("Invalid username or password.")).ToBeVisibleAsync();

            await s.Page.GetByLabel("Username").FillAsync(app.Admin.Username);
            await s.Page.GetByLabel("Password").FillAsync(app.Admin.Password);
            await s.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
            await s.Page.WaitForURLAsync(new Regex("^[^?#]*/$"));
            await Expect(Ui.NewNoteButton(s.Page)).ToBeVisibleAsync();
        }, s);
    }
}
