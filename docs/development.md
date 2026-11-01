# Development

## Project layout

```
JNotes.slnx
src/
  JNotes.Web/     Blazor Server UI + composition root (Program.cs)
  JNotes.Core/     Domain models, markdown/front-matter serialization, file storage, note indexing, EF Core/PostgreSQL
tests/
  JNotes.Tests/    xUnit tests for JNotes.Core
  JNotes.UiTests/  Opt-in Playwright browser tests (drive the real app in-process)
```

`JNotes.Web` never touches the filesystem or EF Core/PostgreSQL directly — it only consumes
`INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`,
`IUserManagementService`, `IImageService`, `IImageCleanupService`, `INoteExportService`, `INoteAutoTagger`, and `INoteSharingService` from `JNotes.Core`. This is enforced at build time via
`Microsoft.CodeAnalysis.BannedApiAnalyzers` (`src/JNotes.Web/BannedSymbols.txt`).

### Styling

The UI is built with [MudBlazor](https://mudblazor.com), pulled in as a NuGet package; its colours come
from the theme in `src/JNotes.Web/Theme/JNotesTheme.cs`. JNotes's own CSS is plain, modern CSS —
hand-authored files served directly from `src/JNotes.Web/wwwroot/css/`, no build step, no Node.js, no CSS
preprocessor — covering only what MudBlazor doesn't. `variables.css` defines app-wide custom properties
(`:root { --note-editor-height: 60vh; ... }`); `note-editor.css`, `note-list.css`, `nav-menu.css` and the
rest are one file per concern, using native CSS nesting (`&`) instead of a Sass equivalent; `app.css`
pulls them all together with native `@import`. Add a new concern as its own `.css` file and `@import` it
from `app.css`.

The Roboto font is vendored under `wwwroot/lib/roboto` (latin subset, SIL OFL 1.1), so the UI makes no
external font requests.

[mermaid](https://mermaid.js.org) (diagram rendering) is vendored the same way under
`wwwroot/lib/mermaid` (pinned to 11.17.2, MIT). The browser only loads it when a note actually
contains a diagram, so JNotes still works with no internet access.

## Tests

```bash
dotnet test JNotes.slnx
# a single test:
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~NoteGroupingTests"
```

The integration and `Users` suites need a **real PostgreSQL** (same `docker run` line as above;
override with `JNOTES_TEST_POSTGRES_CONNECTION_STRING`). Each test class creates and drops its own
`jnotes_test_<guid>` database, so the configured user needs `CREATEDB` (the official image's
`POSTGRES_USER` is a superuser, so the documented `docker run` just works).

### Browser tests (opt-in)

`tests/JNotes.UiTests` is a Playwright (Chromium) suite that starts the real app in the test process, on a free
local port with its own throwaway `jnotes_ui_<guid>` database and temp folders, signs in through the real login
form, and clicks through the UI. It needs the same PostgreSQL as the other tests; one test also needs
[Gotenberg](pdf-export.md) on `http://localhost:3000` and is skipped when it is not there. Plain
`dotnet test JNotes.slnx` builds the project but reports all of its tests as **Skipped**; to run them:

```bash
JNOTES_UI_TESTS=1 dotnet test tests/JNotes.UiTests
JNOTES_UI_TESTS=1 dotnet test tests/JNotes.UiTests --filter "FullyQualifiedName~SharingTests"   # one class
JNOTES_UI_TESTS=1 JNOTES_UI_HEADED=1 JNOTES_UI_SLOWMO=250 dotnet test tests/JNotes.UiTests      # watch it
```

The first run downloads Chromium (about 150 MB, into the Playwright cache, e.g. `~/Library/Caches/ms-playwright`
on macOS); later runs reuse it. Test-only variables:

| Variable | Default | Purpose |
|---|---|---|
| `JNOTES_UI_TESTS` | unset | `1` turns the suite on; otherwise every UI test is Skipped. |
| `JNOTES_UI_HEADED` / `JNOTES_UI_SLOWMO` | off / `0` | Show the browser window / pause this many ms between actions. |
| `JNOTES_UI_GOTENBERG_URL` | `http://localhost:3000` | Gotenberg for the PDF test (Skipped if `/health` doesn't answer). |
| `JNOTES_UI_ARTIFACTS` | `<test output>/ui-artifacts` | Where a failing test writes its screenshot, Playwright trace and problem list. |
| `JNOTES_UI_SKIP_BROWSER_INSTALL` | unset | `1` skips the automatic Chromium install (e.g. you installed it yourself). |

The suite deliberately leaves out what a browser script can't judge: clipboard image paste, how the exported PDF
looks, and dark-theme legibility are still checked by hand.

## Other commands

```bash
dotnet build JNotes.slnx
dotnet format JNotes.slnx
dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
dotnet ef database update --project src/JNotes.Core --startup-project src/JNotes.Core
```

`dotnet ef` is installed as a local tool (`.config/dotnet-tools.json`) — run `dotnet tool restore`
once after cloning if `dotnet ef` isn't found.

## Continuous integration

GitHub Actions (`.github/workflows/`) runs on pull requests to `main` that change anything under `src/`: it
builds the solution and runs the full test suite against a `postgres:18-alpine` service container. No secrets
are needed and nothing is pushed. A pull request that only touches docs, tests or other files outside `src/`
doesn't start the workflow.

Pushes to `main` that change `src/`, and every `v*` tag (GitHub doesn't apply path filters to tags), repeat the build and tests and, only if they pass, also publish
`joeldroid/jnotes` to Docker Hub and `ghcr.io/joeldroid/jnotes` to the GitHub Container Registry
(`linux/amd64` only, the same tags on both) with the tags `latest` and a build version
`{YY}{day of year}{run of the day}` such as `2630501` (both from `main`; the commit gets the same git tag),
and `X.Y.Z`/`X.Y`/`X` (from a version tag). Publishing needs the repository variable
`DOCKERHUB_USERNAME` and the secret `DOCKERHUB_TOKEN` (a Docker Hub access token with Read & Write).
The build version also needs the repository variable `RUN_OF_THE_DAY` (the next run number, starting at `1`;
it is incremented after each tagged build and the first build of a UTC day always uses `1`) and the secret
`VARIABLES_TOKEN`, a fine-grained personal access token limited to this repository with *Variables: read and write*
(the built-in `GITHUB_TOKEN` cannot touch Actions variables). The GitHub Container Registry push needs no extra
secret (it uses the built-in `GITHUB_TOKEN`); a new package starts out private, so make it public once under the
repository's *Packages* → *Package settings* if anyone else should be able to pull it.
