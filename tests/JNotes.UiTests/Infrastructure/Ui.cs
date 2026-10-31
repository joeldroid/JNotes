using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace JNotes.UiTests.Infrastructure;

/// <summary>Small helpers shared by the tests.</summary>
public static partial class Ui
{
    /// <summary>A session user with no cookie: what a visitor who has not signed in looks like.</summary>
    public static UiUser Anonymous { get; } = new(Guid.Empty, "anonymous", "", StorageState: null);

    /// <summary>"Groceries" -> "Groceries 3f9a1c2e": titles are unique per test because the database is shared by the whole run.</summary>
    public static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>The suffix alone, for tags and search tokens.</summary>
    public static string Token() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Waits for one debounced autosave: "Unsaved changes" (the 800 ms debounce guarantees it is observable), then "All changes saved".</summary>
    public static async Task WaitForAutosaveAsync(IPage page)
    {
        await Expect(page.GetByText("Unsaved changes")).ToBeVisibleAsync();
        await Expect(page.GetByText("All changes saved")).ToBeVisibleAsync();
    }

    public static Guid NoteIdFromUrl(string url)
    {
        var match = NoteIdRegex().Match(url);
        Assert.True(match.Success, $"No note id in URL '{url}'.");
        return Guid.Parse(match.Groups[1].Value);
    }

    /// <summary>Clicks a button of the open MudBlazor message box / dialog.</summary>
    public static Task ConfirmDialogAsync(IPage page, string buttonName) =>
        page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = buttonName, Exact = true }).ClickAsync();

    public static Task<string> ListStyleAsync(ILocator element) =>
        element.EvaluateAsync<string>("e => getComputedStyle(e).listStyleType");

    /// <summary>The sidebar's New Note button (a link to <c>/notes/new</c>).</summary>
    public static ILocator NewNoteButton(IPage page) =>
        page.Locator(".jn-nav-new").GetByRole(AriaRole.Link, new() { Name = "New Note" });

    [GeneratedRegex("/notes/([0-9a-fA-F-]{36})")]
    private static partial Regex NoteIdRegex();
}
