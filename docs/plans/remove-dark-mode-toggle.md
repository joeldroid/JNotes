# Plan: Remove the user-selectable light/dark theme; the live app is always dark

## Goal
Settings → App settings → Appearance "Dark mode" switch goes away, and every live page (interactive and static SSR `/login`/`/setup`) always renders with the dark MudBlazor palette and `<html data-theme="dark">`. The per-user theme persistence (`GetThemeAsync`/`SetThemeAsync`, `ThemeState`) is deleted. PDF export stays light-themed through its own independent path, which is not touched.

## Context found
- **Core persistence**
  - `C:\Data\Code\JNotes\src\JNotes.Core\Settings\ISettingsService.cs` declares `GetThemeAsync(Guid userId, …)` and `SetThemeAsync(Guid userId, string theme, …)` on lines 18–21.
  - `C:\Data\Code\JNotes\src\JNotes.Core\Settings\SettingsService.cs` implements them on lines 53–76, with `private const string ThemeKey = "Theme";` on line 14. It stores a **row** in `db.UserSettings` keyed `(userId, "Theme")`.
  - `C:\Data\Code\JNotes\src\JNotes.Core\Settings\AppTheme.cs` is a static class with `Light = "light"` and `Dark = "dark"`. Its doc comment is stale: it mentions Bootstrap `data-bs-theme`.
- **The theme is not a column.** `C:\Data\Code\JNotes\src\JNotes.Core\Data\UserSetting.cs` is a generic `(UserId, Key, Value)` key/value entity. It is configured in `JNotesDbContext.cs` (lines 66–73, composite PK, cascade FK to Users) and created in `Data\Migrations\20260927025445_InitialCreate.cs`. "Theme" is its only current use, but the table is a generic per-user settings store. **No schema change or migration is needed.**
- **Web plumbing**
  - `C:\Data\Code\JNotes\src\JNotes.Web\Theme\ThemeState.cs` is a scoped `IsDark` holder with a `Changed` event.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Program.cs` has `builder.Services.AddScoped<ThemeState>();` on line 109. Line 16 has `using JNotes.Web.Theme;`, whose only use in that file is `ThemeState`.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Components\Layout\MainLayout.razor`
    - Injects `AuthenticationStateProvider`, `ISettingsService`, `ICurrentUserContext`, `ThemeState` and `IJSRuntime`. All five are used only for the theme.
    - Renders `<MudThemeProvider … IsDarkMode="Theme.IsDark" ObserveSystemDarkModeChange="false" />`.
    - `OnInitializedAsync` seeds `ThemeState` from `GetThemeAsync`.
    - `OnThemeChanged` rewrites `data-theme` over JS interop.
    - `Dispose` unsubscribes `Theme.Changed` and `Navigation.LocationChanged`. The `LocationChanged`/`Viewport` drawer logic is unrelated and stays.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Components\App.razor` has `<html lang="en" data-theme="@_theme">`, `_theme = AppTheme.Dark`, and an `OnInitializedAsync` that calls `GetThemeAsync` for authenticated requests and uses Dark otherwise. It injects `ISettingsService` and `ICurrentUserContext` only for this.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Components\Layout\AuthLayout.razor` already has `IsDarkMode="true"` hardcoded (static SSR `/login`/`/setup`). No change is needed there.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Components\Pages\Settings.razor`
    - Line 20: `@inject ThemeState Theme`.
    - Lines 30–33: the "Appearance" `MudPaper` with `<MudSwitch … ValueChanged="ToggleThemeAsync" Label="Dark mode" …/>`.
    - Line 341: `_theme` field.
    - Line 398: the first statement of `OnInitializedAsync` is `_theme = await SettingsService.GetThemeAsync(CurrentUser.UserId);`.
    - Lines 424–433: `ToggleThemeAsync`.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Theme\JNotesTheme.cs` sets both `PaletteLight` and `PaletteDark`.
  - `C:\Data\Code\JNotes\src\JNotes.Web\Components\_Imports.razor` has `@using JNotes.Web.Theme` (still needed for `JNotesTheme`) and `@using JNotes.Core.Settings` (still needed for `ISettingsService`, `OidcOptions` and others).
- **JS:** `C:\Data\Code\JNotes\src\JNotes.Web\wwwroot\js\mermaid-preview.js`
  - `currentTheme()` (line 25) reads `data-theme === 'dark'`.
  - A `MutationObserver` (lines 94–112) re-renders diagrams when the attribute flips.
  - The comment on line 99 mentions "Settings -> Dark mode".
  - With the attribute fixed at `"dark"`, the observer never fires. That is harmless, so the logic stays as-is.
- **No app CSS keys off `data-theme` or `prefers-color-scheme`.** Grepped all of `src/JNotes.Web` except `wwwroot/lib`. `MainLayout.razor.css`'s `color-scheme: light only; background: lightyellow` is the `#blazor-error-ui` banner. It is unrelated; leave it.
- **PDF path (independent, DO NOT TOUCH)**
  - `C:\Data\Code\JNotes\src\JNotes.Core\Export\note-pdf.css` uses literal light colours and `color-scheme: light`.
  - `C:\Data\Code\JNotes\src\JNotes.Core\Export\NotePdfDocumentBuilder.cs` calls `mermaid.initialize({… theme: 'default' })` on line 34.
  - Neither file reads `ISettingsService`, `AppTheme` or `data-theme`.
- **Tests**
  - `C:\Data\Code\JNotes\tests\JNotes.Tests\TestDoubles\StubSettingsService.cs` implements `ISettingsService` with `_themesByUser`, `GetThemeAsync` and `SetThemeAsync` (lines 10, 24–31). It **must** be updated or the test project won't compile.
  - `C:\Data\Code\JNotes\tests\JNotes.Tests\Integration\SettingsServiceTests.cs` has no theme tests.
  - The `mermaid theme=dark` in `Serialization\MarkdownRendererTests.cs` (line 77) is a fence info string, unrelated. Leave it.
- **Docs**
  - `C:\Data\Code\JNotes\README.md` line 22: "with a light/dark theme". Line 209: "The PDF is always light-themed, whatever your theme."
  - `C:\Data\Code\JNotes\CLAUDE.md` describes the toggle on lines 106–110, 114–119, 178 and 462–464.
  - `C:\Data\Code\JNotes\src\JNotes.Core\Users\ICurrentUserContext.cs` line 5 mentions "the Settings page's per-user theme lookups".
  - Historical plans in `docs/plans/*.md` also mention the toggle. They are records; leave them.
- **Project rules that apply**
  - The `ICurrentUserContext` gotcha: `Settings.razor` still `@inject`s `ICurrentUserContext`, so it is constructed at component activation. Removing the theme call from `OnInitializedAsync` does not defer it. `NavMenu.razor` also injects `ICurrentUserContext` and renders inside `MainLayout`, so removing `MainLayout`'s injection does not change resolution timing.
  - `EnforceCodeStyleInBuild` is on (`Directory.Build.props`). Remove now-unused `using`s and `@inject`s, and run `dotnet format --verify-no-changes`.

## Steps

### Phase A: Web stops using the theme API (still builds; the Core API is still present)

1. **`src\JNotes.Web\Components\Pages\Settings.razor`**
   - Delete `@inject ThemeState Theme` (line 20).
   - Delete the whole Appearance card, lines 30–33 (the `<MudPaper Outlined="true" Class="jn-settings-card">` containing the "Appearance" `MudText` and the Dark mode `MudSwitch`). The "App settings" section then contains only the Account card. Keep the section heading.
   - In `@code`, delete the `_theme` field and the `// App settings` comment directly above it, or keep the comment for `_newPassword`.
   - Delete the first line of `OnInitializedAsync` (`_theme = await SettingsService.GetThemeAsync(...)`).
   - Delete the `ToggleThemeAsync` method entirely.
   - Leave `AuthenticationStateProvider`, `ICurrentUserContext` and `ISettingsService` injected; other code uses them.
2. **`src\JNotes.Web\Components\Layout\MainLayout.razor`**
   - Remove the injections of `AuthenticationStateProvider`, `ISettingsService`, `ICurrentUserContext`, `ThemeState` and `IJSRuntime`.
   - Keep `IBrowserViewportService Viewport` and `NavigationManager Navigation`, plus `@implements IDisposable`.
   - Change the provider to `<MudThemeProvider Theme="JNotesTheme.Instance" IsDarkMode="true" ObserveSystemDarkModeChange="false" />`.
   - Replace `OnInitializedAsync` with a synchronous `protected override void OnInitialized() => Navigation.LocationChanged += OnLocationChanged;`, or an equivalent block body.
   - Delete `OnThemeChanged` entirely.
   - In `Dispose`, drop the `Theme.Changed -=` line and keep the `LocationChanged` unsubscribe.
   - Add a one-line comment above the provider: the app is dark-only, and `data-theme="dark"` is set once in `App.razor`.
3. **`src\JNotes.Web\Components\App.razor`**
   - Remove `@inject ISettingsService SettingsService` and `@inject ICurrentUserContext CurrentUser`.
   - Change line 5 to `<html lang="en" data-theme="dark">` as a literal.
   - Delete the `_theme` field and the whole `OnInitializedAsync` override with its comment. Keep the `HttpContext` cascading parameter and `RenderModeForPage`, which are still needed.
   - Add a short comment beside the `<html>` tag or in `@code`: `data-theme` is fixed to `dark` because `mermaid-preview.js` reads it, and there is no user theme.
   - Why a literal: `<html>` is outside any interactive root, so it keeps the prerendered value for the life of the page, and enhanced navigation re-serves the same value.
4. **`src\JNotes.Web\Program.cs`**: delete `builder.Services.AddScoped<ThemeState>();` (line 109). Delete `using JNotes.Web.Theme;` (line 16) if nothing else in the file uses that namespace (grep `JNotesTheme` in `Program.cs`; currently none).
5. **Delete `src\JNotes.Web\Theme\ThemeState.cs`.** Keep the `Theme` folder, `JNotesTheme.cs` and the `_Imports.razor` `@using JNotes.Web.Theme`.
6. **`src\JNotes.Web\Theme\JNotesTheme.cs`**
   - Remove the `PaletteLight = new PaletteLight { … },` initializer. `MudTheme` creates a default `PaletteLight` itself, and it is never emitted because `IsDarkMode` is always true.
   - Keep `PaletteDark`, `LayoutProperties` and `Typography` unchanged.
   - Update the class doc comment to say the app is dark-only (`IsDarkMode="true"` in both `MainLayout` and `AuthLayout`).
7. **`src\JNotes.Web\wwwroot\js\mermaid-preview.js`**: change only the comment on lines 99–100. Suggested text: "The app is dark-only today (App.razor fixes data-theme="dark"), but the theme stays attribute-driven and observed so diagrams follow `<html data-theme>` if that ever changes." Do not change `currentTheme()`, the observer or the cache key. It stays correct with a fixed value and is the least risky choice.
8. Build: `dotnet build C:\Data\Code\JNotes\JNotes.slnx`. It must succeed with no new warnings.

### Phase B: Remove the theme API from Core (and the test stub in the same phase, so the solution keeps building)

9. **`src\JNotes.Core\Settings\ISettingsService.cs`**: delete `GetThemeAsync` and `SetThemeAsync` and their `<summary>` (lines 18–21).
10. **`src\JNotes.Core\Settings\SettingsService.cs`**: delete `ThemeKey` (line 14) and both methods (lines 53–76). `UserSetting` is then unused here. Leave the `using JNotes.Core.Data;` because `SettingEntry` and `JNotesDbContext` still need it.
11. **Delete `src\JNotes.Core\Settings\AppTheme.cs`.** After Phase A nothing references it. Confirm with `Grep "AppTheme"` over `src` and `tests`, which should find no matches.
12. **`tests\JNotes.Tests\TestDoubles\StubSettingsService.cs`**: delete the `_themesByUser` field and the `GetThemeAsync`/`SetThemeAsync` members (lines 10, 24–31).
13. **`src\JNotes.Core\Data\UserSetting.cs`**: keep the class. Only change its doc-comment example `(e.g. "Theme")` to something neutral, for example "no keys are currently in use; the table is kept for future per-user preferences".
14. **`src\JNotes.Core\Users\ICurrentUserContext.cs`** line 5: change "`NoteService` and the Settings page's per-user theme lookups depend on this" to "`NoteService` (and other implicit-current-user Core services) depend on this". The comment also mentions a stale `CookieCurrentUserContext`; optionally fix it to `AuthenticatedCurrentUserContext`.
15. **No EF migration.** `JNotesDbContext` and the model are unchanged. Run `dotnet ef migrations has-pending-model-changes --project src/JNotes.Core --startup-project src/JNotes.Core` (after `dotnet tool restore`). It should report no changes. Existing `UserSettings` rows with `Key = 'Theme'` (including `'light'`) are now ignored. They are harmless and are deleted by the existing cascade when a user is deleted. Do **not** add a data-cleanup migration (see Open questions).
16. `dotnet build JNotes.slnx`, then `dotnet format JNotes.slnx --verify-no-changes`.

### Phase C: README

17. **`README.md`**
    - Line 22: change "(Material design) with a light/dark theme, usable on both desktop and mobile." to "(Material design) with a dark theme, usable on both desktop and mobile."
    - Line 209: change "The PDF is always light-themed, whatever your theme." to "The PDF is always light-themed, even though the app itself is dark."
    - Leave line 16 ("light-themed A4 PDF") as it is.

### Phase D: CLAUDE.md (only if the user approves editing it; see Open questions)

18. **`CLAUDE.md`**
    - **Theme** paragraph (lines 106–110): replace with "The live app is dark-only: `MainLayout` and `AuthLayout` render `MudThemeProvider IsDarkMode="true"`, and `App.razor` emits a literal `<html data-theme="dark">` (read by `mermaid-preview.js`). There is no per-user theme setting. The `UserSettings` table is kept, unused, for future per-user preferences. PDF export is light via its own `note-pdf.css`, independent of this."
    - **"One theme, one owner"** bullet (lines 114–119): drop the `ThemeState` seeding and the Settings dark-mode switch sentence. Keep that `MainLayout` hosts the providers.
    - Line 178: change "(Appearance, your own password)" to "(your own password)".
    - Lines 462–464: say the attribute is fixed to `dark` today, and the observer and theme cache key are kept so a future theme change needs no JS changes.

## Tests to add or update
- **Update:** `tests\JNotes.Tests\TestDoubles\StubSettingsService.cs` (step 12); this is required for compilation.
- **No deletions:** no test covers theme behaviour. `Integration\SettingsServiceTests.cs` tests only image-cleanup settings.
- **No new tests:** removing the members from `ISettingsService` is a compile-time guarantee. The UI changes have no bUnit coverage by project convention and are checked manually.
- Run `dotnet test C:\Data\Code\JNotes\JNotes.slnx`. The integration tests need `jnotes-debug-db` on `localhost:5432`, which is the tests' default connection string. Do not stop or recreate that container.

## Manual verification (for the tester)
Setup: use a throwaway database and notes folder, never the user's `jnotes` DB or `App_Data/notes`. The launch profile's env values override shell env, so run without it. In PowerShell from `C:\Data\Code\JNotes`:

    $env:ASPNETCORE_ENVIRONMENT="Development"; $env:ASPNETCORE_URLS="http://localhost:5231"
    $env:POSTGRES_HOST="localhost"; $env:POSTGRES_PORT="5432"; $env:POSTGRES_USER="jnotes"; $env:POSTGRES_PASSWORD="jnotes"
    $env:POSTGRES_DB="jnotes_manual_theme"
    $env:NOTES_PATH="C:\Users\Joel\AppData\Local\Temp\claude\C--Data-Code-JNotes\dced776c-09ae-40f3-8dbb-01ce79689eac\scratchpad\notes"
    $env:DATA_PROTECTION_KEYS_PATH="C:\Users\Joel\AppData\Local\Temp\claude\C--Data-Code-JNotes\dced776c-09ae-40f3-8dbb-01ce79689eac\scratchpad\keys"
    $env:GOTENBERG_URL="http://localhost:3000"; $env:OLLAMA_URL=""
    dotnet run --project src/JNotes.Web --no-launch-profile

(`jnotes-debug-pdf` must be running on `localhost:3000`. Do not `docker compose up` the whole debug file.)

1. Open `http://localhost:5231/`. It redirects to `/setup` (fresh DB). The page is dark. In DevTools, `document.documentElement.dataset.theme === "dark"`.
2. Create the admin. After redirect, `/login` is dark. Sign in. `/` renders dark (dark app bar, drawer and background), and `<html data-theme="dark">`.
3. Open `/settings`. "App settings" shows only the **Account** card: no "Appearance" heading and no "Dark mode" switch. The Administration section renders normally, and the password change still works. The browser console has no errors.
4. As admin, add a Member user, sign out, and sign in as the Member. `/settings` shows only the Account card and no Administration section. The whole app is dark.
5. Create a text note whose body contains ` ```mermaid ` / `flowchart LR` / `A --> B` / ` ``` `. In the editor preview and on `/notes/{id}`, the diagram renders in mermaid's **dark** theme (dark node fills and light text).
6. On that note, click **Export PDF**. The new tab shows a **light** PDF (white background, dark text) with the diagram in mermaid's light/default theme.
7. Visit a nonexistent route such as `/does-not-exist`. The NotFound page (anonymous `MainLayout`) renders dark without errors.
8. Reload `/settings` and navigate between pages. There is no flash of light theme.
9. Afterwards, stop the app. Optionally drop the throwaway DB: `docker exec jnotes-debug-db psql -U jnotes -d jnotes -c 'DROP DATABASE "jnotes_manual_theme" WITH (FORCE);'`. Delete the scratchpad notes and keys folders.

Commands for the tester:
- `dotnet build JNotes.slnx`
- `dotnet format JNotes.slnx --verify-no-changes`
- `dotnet test JNotes.slnx`
- `dotnet ef migrations has-pending-model-changes --project src/JNotes.Core --startup-project src/JNotes.Core` (expect: no changes)

## Risks and edge cases
- **Existing users who chose light** silently become dark. Their `UserSettings` "Theme" row stays in the DB, unused. This is intended; mention it in the commit message.
- **`ICurrentUserContext` timing:** `MainLayout` no longer injects it. `NavMenu` (rendered by `MainLayout` on the same first render) and every `[Authorize]` page still resolve it early, so nothing changes. Verify steps 2–4 have no "not authenticated" or HttpContext exceptions in the console log.
- **`Settings.razor`'s first awaited call** is now `IsCurrentUserAdminAsync()`. `CurrentUser` and `NoteService` are `@inject`ed, so they are still constructed at activation. Nothing is deferred.
- **Unused `using`/`@inject` lines** can fail `dotnet format --verify-no-changes` (`EnforceCodeStyleInBuild`). Remove them as listed.
- **Dropping `PaletteLight`:** MudBlazor falls back to its default light palette object. That object is never used, because every `MudThemeProvider` has `IsDarkMode="true"`. If the build flags something about `PaletteLight` being required (it shouldn't; it's a settable property with a default), keep the block instead.
- **The PDF path is independent:** it uses `note-pdf.css` and a hardcoded `theme: 'default'` in `NotePdfDocumentBuilder`. Do not edit either file. Step 6 of the manual checks confirms it is unaffected.

## Open questions
- **Clean up the stale `UserSettings` rows with `Key = 'Theme'`?** Recommended default: **no**. They are inert, cascade-deleted with their user, and a data migration adds risk for no user-visible benefit. If it is ever wanted, it would be an empty migration (`dotnet ef migrations add RemoveThemeSetting --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations`) with a hand-written `migrationBuilder.Sql("DELETE FROM \"UserSettings\" WHERE \"Key\" = 'Theme';")`.
- **Drop the now-unused `UserSettings` table and `UserSetting` entity?** Recommended default: **no**. It is a generic per-user preferences store, and dropping it needs a schema migration for no functional gain.
- **Edit `CLAUDE.md` (Phase D)?** It documents the toggle in detail and would mislead future work if left as it is. Recommended default: **yes, with the user's explicit approval**. If not approved, skip Phase D and flag that the file is stale.

## Out of scope
- `src\JNotes.Core\Export\note-pdf.css`, `src\JNotes.Core\Export\NotePdfDocumentBuilder.cs`, `PdfMarkdownRenderer` and anything else under `src\JNotes.Core\Export\`, plus `Export\NotePdfEndpoints.cs` in Web. **Do not touch**: the PDF must stay light.
- `JNotesDbContext`, `UserSetting`'s shape, and any file under `src\JNotes.Core\Data\Migrations\`. No migration.
- `AuthLayout.razor` (already `IsDarkMode="true"`), `auth.css`, and the `MainLayout.razor.css` error-banner styling.
- `mermaid-preview.js` logic (comment only) and the vendored `wwwroot\lib\**`.
- Historical plan docs under `docs\plans\` (other than creating this one).
- The `jnotes-debug-*` containers and the user's `jnotes` database and `App_Data`.
