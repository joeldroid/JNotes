using System.Diagnostics;
using System.Text.RegularExpressions;
using JNotes.UiTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests;

[Collection(UiCollection.Name)]
public sealed class EditorTests(UiAppFixture app, ITestOutputHelper output)
{
    [UiFact]
    public async Task Edit_autosave_Close_persists()
    {
        var title = Ui.Unique("Autosave");
        var id = await app.SeedNoteAsync(app.Admin, title, "original body");
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync($"/notes/{id}/edit");
            var body = s.Page.GetByLabel("Note body");
            await Expect(body).ToHaveValueAsync("original body");

            var marker = "typed " + Ui.Token();
            await body.FillAsync("original body\n\n" + marker);
            await Ui.WaitForAutosaveAsync(s.Page);

            await s.Page.GetByRole(AriaRole.Link, new() { Name = "Close" }).ClickAsync();
            await s.Page.WaitForURLAsync(new Regex($"/notes/{id}$"));
            await Expect(s.Page.Locator(".markdown-preview")).ToContainTextAsync(marker);

            await app.ReadNoteFileEventuallyAsync(app.Admin, id, text => text.Contains(marker, StringComparison.Ordinal));
        }, s);
    }

    [UiFact]
    public async Task Clicking_New_Note_right_after_an_edit_keeps_the_edit_and_shows_no_error()
    {
        var title = Ui.Unique("Race");
        var id = await app.SeedNoteAsync(app.Admin, title, "original body");
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            await s.GotoInteractiveAsync($"/notes/{id}/edit");
            var body = s.Page.GetByLabel("Note body");
            await Expect(body).ToHaveValueAsync("original body");

            var marker = "edited " + Ui.Token();
            var clock = Stopwatch.StartNew();
            await body.FillAsync("original body\n" + marker); // one input event; the 800 ms content debounce starts
            await Ui.NewNoteButton(s.Page).ClickAsync();
            Assert.True(
                clock.ElapsedMilliseconds < 700,
                $"New Note was clicked {clock.ElapsedMilliseconds} ms after the edit; the race window (800 ms debounce) was missed, so the test proved nothing.");

            await s.Page.WaitForURLAsync(new Regex("/notes/new$"));
            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("");
            await s.Page.WaitForTimeoutAsync(1500); // past both debounces: a stale save would surface now
            await Expect(s.Page.GetByText("no longer available")).ToHaveCountAsync(0);
            await Expect(s.Page.GetByText("Autosave failed")).ToHaveCountAsync(0);

            var file = await app.ReadNoteFileEventuallyAsync(app.Admin, id, text => text.Contains(marker, StringComparison.Ordinal));
            Assert.Contains("original body", file);

            await s.GotoInteractiveAsync($"/notes/{id}");
            await Expect(s.Page.Locator(".markdown-preview")).ToContainTextAsync(marker);
        }, s);
    }

    [UiFact]
    public async Task Switching_straight_to_another_notes_editor_keeps_the_edit_and_leaves_the_other_note_alone()
    {
        var idA = await app.SeedNoteAsync(app.Admin, Ui.Unique("SwitchA"), "body of A");
        var idB = await app.SeedNoteAsync(app.Admin, Ui.Unique("SwitchB"), "body of B");
        await using var s = await UiSession.StartAsync(app, app.Admin, output);
        await UiSession.RunAsync(async () =>
        {
            var fileBBefore = await app.ReadNoteFileEventuallyAsync(app.Admin, idB, _ => true);

            await s.GotoInteractiveAsync($"/notes/{idA}/edit");
            var body = s.Page.GetByLabel("Note body");
            await Expect(body).ToHaveValueAsync("body of A");

            var marker = "edited " + Ui.Token();
            var clock = Stopwatch.StartNew();
            await body.FillAsync("body of A\n" + marker); // one input event; the 800 ms content debounce starts
            // A client-side navigation to the other note's editor: /notes/{id}/edit -> /notes/{id}/edit reuses this
            // component instance (no full page load), so only the Id parameter changes.
            await s.Page.EvaluateAsync("path => Blazor.navigateTo(path)", $"/notes/{idB}/edit");
            Assert.True(
                clock.ElapsedMilliseconds < 700,
                $"The switch happened {clock.ElapsedMilliseconds} ms after the edit; the race window (800 ms debounce) was missed, so the test proved nothing.");

            await s.Page.WaitForURLAsync(new Regex($"/notes/{idB}/edit$"));
            await Expect(s.Page.GetByLabel("Note body")).ToHaveValueAsync("body of B");
            await s.Page.WaitForTimeoutAsync(1500); // past both debounces: a stale save would surface now
            await Expect(s.Page.GetByText("no longer available")).ToHaveCountAsync(0);
            await Expect(s.Page.GetByText("Autosave failed")).ToHaveCountAsync(0);

            var fileA = await app.ReadNoteFileEventuallyAsync(app.Admin, idA, text => text.Contains(marker, StringComparison.Ordinal));
            Assert.Contains("body of A", fileA);

            // The debounce that was pending for A must not have re-saved B (that would rewrite its file and bump UpdatedAt).
            var fileBAfter = await app.ReadNoteFileEventuallyAsync(app.Admin, idB, _ => true);
            Assert.Equal(fileBBefore, fileBAfter);
        }, s);
    }
}
