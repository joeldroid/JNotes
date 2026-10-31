<p align="center">
  <img src="src/JNotes.Web/wwwroot/img/jnotes-logo-512.png" alt="JNotes logo" width="160" height="160">
</p>

<h1 align="center">JNotes</h1>

<p align="center">
  <strong>A self-hosted, multi-user note-taking app. Your notes stay plain markdown files you own.</strong>
</p>

<p align="center">
  <a href="https://github.com/joeldroid/JNotes/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/joeldroid/JNotes/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/joeldroid/JNotes/actions/workflows/docker-publish.yml"><img alt="Publish" src="https://github.com/joeldroid/JNotes/actions/workflows/docker-publish.yml/badge.svg"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="Blazor Server" src="https://img.shields.io/badge/Blazor-Server-512BD4?logo=blazor&logoColor=white">
  <img alt="PostgreSQL" src="https://img.shields.io/badge/PostgreSQL-index%20%26%20accounts-4169E1?logo=postgresql&logoColor=white">
  <img alt="Docker" src="https://img.shields.io/badge/Docker-ready-2496ED?logo=docker&logoColor=white">
</p>

<p align="center">
  <a href="#features">Features</a> ·
  <a href="#running-locally">Running locally</a> ·
  <a href="#running-with-docker">Docker</a> ·
  <a href="#authentication">Authentication</a> ·
  <a href="#sharing-notes">Sharing</a> ·
  <a href="#note-file-format">File format</a>
</p>

---

JNotes is built with Blazor Server (.NET 10). Notes are stored
as portable markdown files on disk — copy the folder and every note is a readable `.md` file in
any editor. A single PostgreSQL database holds user accounts and settings, and the note index that
makes notes fast to search/list; the index tables are a derived cache, never the source of truth —
the markdown files always are.

The very first time you run JNotes, you'll land on a setup screen to create the admin account —
see [Authentication](#authentication) below for that flow, adding more users, and OIDC.

## Features

- **Notes** — free-form markdown, edited with a live preview, with `mermaid` code blocks rendered as diagrams (toolbar buttons insert a starter flow chart), and images inserted with the **Image** button or by pasting one (PNG, JPEG, GIF or WebP, up to 10 MB).
- **Task lists** — markdown task lists (`- [ ] item` / `- [x] item`) render as read-only checkboxes in previews and in the PDF export; edit the markdown to change them.
- **PDF export (optional)** — an **Export PDF** button on a note opens it as a light-themed A4 PDF (images and diagrams included), rendered by a [Gotenberg](https://gotenberg.dev) container. Off unless `GOTENBERG_URL` is set.
- **Auto-tagging (optional)** — when `OLLAMA_URL` points at an [Ollama](https://ollama.com) server, a background task tags notes that have no tags using a local model (Microsoft's `phi4` by default). It never changes tags you've written.
- **Note sharing (Read/Edit)** — share a single note with any other user, as view-only or editable (body only); shared notes appear in **All Notes**, in search and on a **Shared with Me** page. See [Sharing notes](#sharing-notes).
- **Multi-user with real accounts** — admin-created users, each with their own notes and settings;
  sign in with a password, OIDC, or both.
- **Portable storage** — every note is `notes/<user-id>/<id>.md` with a small YAML front matter
  header (title, tags, timestamps) followed by the markdown body.
- **Recent notes and Trash** — the sidebar lists your ten most recently updated notes, with **Trash** pinned to the bottom; deleting a note moves it to Trash until you delete it permanently.
- **Responsive UI** — [MudBlazor](https://mudblazor.com) (Material design) with a dark theme, usable on both desktop and mobile.
- **Self-healing index** — the note index is reconciled against the notes folder at startup and
  on demand (Administration → Rescan Now), so edits made directly to the files (outside the app, or
  while the container was stopped) are picked up automatically.

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

## Running locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/JNotes.Web
# or, for auto-rebuild on change:
dotnet watch --project src/JNotes.Web run
```

All configuration is environment variables only — there is no `appsettings.json`, and command-line
arguments are not a configuration source. For local development the launch profiles in
`src/JNotes.Web/Properties/launchSettings.json` set the variables for you (`dotnet run`,
`dotnet watch` and Visual Studio F5 all use them). In particular the profile sets
`NOTES_PATH=App_Data/notes`, so notes are stored under `src/JNotes.Web/App_Data/` — gitignored,
safe to delete to start fresh. Override any variable in your own environment the same way
`docker-compose.yml` does (e.g. it points `NOTES_PATH` at `/data`).

**PostgreSQL is required to run at all** — it holds user accounts, settings, and the note index
(everything `NOTES_PATH`'s files are indexed into for listing/search). Locally, e.g.:

```bash
docker run --rm -p 5432:5432 -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine
```

then the launch profiles in `src/JNotes.Web/Properties/launchSettings.json` already point
`POSTGRES_HOST`/`POSTGRES_PORT`/`POSTGRES_DB`/`POSTGRES_USER`/`POSTGRES_PASSWORD` at that container. The index (the
`NoteIndex*` tables) is a rebuildable cache: if it's empty (fresh database, truncated tables), the
startup reconciliation pass rebuilds it from the markdown files, so it needs no backup of its own.
Reconciliation also runs on a timer (`AUTO_INDEX_INTERVAL_MINUTES`, see below), so externally
edited/added/removed `.md` files are picked up without a restart. The accounts/settings tables
beside it are **not** rebuildable and do need a backup — see the `pg_dump` callout below.

On first run with no users in the database, every request redirects to a setup screen where you
pick the admin's username and password — see [Authentication](#authentication) below.

### Configuration

| Environment variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `POSTGRES_HOST` | **Yes** | none — startup fails without it. `localhost` in the launch profiles / `postgres` in Docker (the compose service name) | Host of the one PostgreSQL database holding user accounts, settings and the note index. There's no fallback, and a missing or unreachable database fails startup rather than starting degraded. |
| `POSTGRES_DB` | **Yes** | none — startup fails without it. `jnotes` in the launch profiles; `${POSTGRES_DB:-jnotes}` in compose | Database name. |
| `POSTGRES_USER` | **Yes** | none — startup fails without it. `jnotes` in the launch profiles; `${POSTGRES_USER:-jnotes}` in compose | Database username. |
| `POSTGRES_PASSWORD` | No (needed for password auth) | none. `jnotes` in the launch profiles; `${POSTGRES_PASSWORD:-jnotes}` in compose | Database password. Never logged. Change this for anything reachable beyond your machine. |
| `POSTGRES_PORT` | No | `5432` | PostgreSQL port. |
| `NOTES_PATH` | No | `notes` (relative to the working directory). `App_Data/notes` in the launch profiles / `/data/notes` in Docker | Folder note markdown files are read from/written to. |
| `DATA_PROTECTION_KEYS_PATH` | No | a `keys` folder beside `NOTES_PATH`. `App_Data/keys` in the launch profiles / `/data/keys` in Docker | Where the keys that sign the auth cookie are stored. Must be on persistent storage — on a container's ephemeral filesystem every restart regenerates them and silently signs everyone out. |
| `TZ` | No | `UTC` (`Australia/Melbourne` in the launch profiles) | IANA time zone id (e.g. `America/New_York`, `Europe/London`) note timestamps are *displayed* in. Storage is always UTC regardless — this only affects what you see in the UI. |
| `AUTO_INDEX_INTERVAL_MINUTES` | No | `5` | How often the background pass reconciles every user's note index against the files on disk. `0` disables it (the startup pass and Administration → "Rescan Now" still work). |
| `OIDC_AUTHORITY` | No | unset | Your OIDC provider's issuer URL. Leave unset to disable OIDC entirely. Not set by the launch profiles, so values in your own environment flow through. |
| `OIDC_CLIENT_ID` | No | unset | OIDC client id registered with your provider. |
| `OIDC_CLIENT_SECRET` | No | unset | OIDC client secret. All three `OIDC_*` values must be set for OIDC to be offered. |
| `OIDC_DISPLAY_NAME` | No | `OIDC` | Label on the "Sign in with …" button on the login page. |
| `GOTENBERG_URL` | No | unset (PDF export off). `http://localhost:3000` in the launch profiles | Base URL of a [Gotenberg 8](https://gotenberg.dev) service (e.g. `http://gotenberg:3000`) used to render note PDFs. Leave unset to disable PDF export entirely — no button, and `/notes/{id}/pdf` returns 404. Must be an absolute http(s) URL; anything else logs a warning and leaves it off. |
| `OLLAMA_URL` | No | unset (auto-tagging off). Set to empty in the launch profiles | Base URL of an [Ollama](https://ollama.com) server (e.g. `http://localhost:11434`); enables background auto-tagging. Must be an absolute http(s) URL, otherwise a warning is logged and it stays off; every user's untagged note text is sent here. See [Auto-tagging with Ollama](#auto-tagging-with-ollama-optional). |
| `OLLAMA_MODEL` | No | `phi4` | The Ollama model used to suggest tags (e.g. `phi4-mini`, or a pinned `phi4:14b`). It must already be pulled on the server. |
| `OLLAMA_TIMEOUT_SECONDS` | No | `120` | How long to wait for the model before giving up (10–600). A big model on CPU, or a cold model load, can be slow. |
| `AUTO_TAG_INTERVAL_MINUTES` | No | `10` | Minutes between background auto-tagging passes (1–1440). |
| `AUTO_TAG_BATCH_SIZE` | No | `10` | Most notes tagged per pass, across all users (1–100). |
| `AUTO_TAG_ENABLED` | Deprecated | ignored | No longer read; a warning is logged if it is still set. Auto-tagging is controlled by `OLLAMA_URL` alone. |
| `ASPNETCORE_ENVIRONMENT` | No | `Production` (`Development` in the launch profiles) | ASP.NET Core host environment (developer exception page, DI validation, static web assets). |
| `ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS` | No | Kestrel's default (`ASPNETCORE_URLS=http://+:8080` in the Docker image) | Listen addresses. The launch profiles set them from `applicationUrl`. |
| `Logging__LogLevel__Default` | No | `Information` | Standard ASP.NET Core log level override. |
| `Logging__LogLevel__Microsoft.AspNetCore` | No | `Warning` | Standard ASP.NET Core log level override (set to `Information` to see per-request lines). |
| `AllowedHosts` | No | `*` | ASP.NET Core host filtering. |

Running without the launch profile (`dotnet run --no-launch-profile`, a published build) requires setting
these yourself; a missing required one stops startup with a message naming it. Blank values count as
unset. The remaining variables, `JNOTES_TEST_POSTGRES_CONNECTION_STRING` and the `JNOTES_UI_*` switches (see
[Tests](#tests)), are read only by the test suites, never by the app.

All of these take effect at startup only — changing one requires an app/container restart.

> **What are the Data Protection keys, and why do they need their own path?** ASP.NET Core signs the
> auth cookie with them — without persisting them somewhere durable, every restart generates a fresh
> set and silently signs everyone out (nothing errors; sessions just stop working). A PostgreSQL
> connection string doesn't point at a local path, so they have their own setting,
> `DATA_PROTECTION_KEYS_PATH`. Left unset, it falls back to a `keys` folder beside `NOTES_PATH` — just
> make sure wherever it points is on persistent storage, not a container's ephemeral filesystem.

> **Backing up the database?** Use `pg_dump` (e.g.
> `docker compose exec postgres pg_dump -U jnotes jnotes > jnotes.sql`), not a plain file copy —
> copying the volume's files while the server is running is not a backup. Your notes themselves are
> always plain `.md` files, safe to copy anytime, and the `NoteIndex*` tables in that dump are
> rebuildable anyway.

### Tests

```bash
dotnet test JNotes.slnx
# a single test:
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~NoteGroupingTests"
```

The integration and `Users` suites need a **real PostgreSQL** (same `docker run` line as above;
override with `JNOTES_TEST_POSTGRES_CONNECTION_STRING`). Each test class creates and drops its own
`jnotes_test_<guid>` database, so the configured user needs `CREATEDB` (the official image's
`POSTGRES_USER` is a superuser, so the documented `docker run` just works).

#### Browser tests (opt-in)

`tests/JNotes.UiTests` is a Playwright (Chromium) suite that starts the real app in the test process, on a free
local port with its own throwaway `jnotes_ui_<guid>` database and temp folders, signs in through the real login
form, and clicks through the UI. It needs the same PostgreSQL as the other tests; one test also needs
[Gotenberg](#pdf-export-optional) on `http://localhost:3000` and is skipped when it is not there. Plain
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

### Other commands

```bash
dotnet build JNotes.slnx
dotnet format JNotes.slnx
dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
dotnet ef database update --project src/JNotes.Core --startup-project src/JNotes.Core
```

`dotnet ef` is installed as a local tool (`.config/dotnet-tools.json`) — run `dotnet tool restore`
once after cloning if `dotnet ef` isn't found.

### Continuous integration

GitHub Actions (`.github/workflows/`) runs on every pull request to `main`: it builds the solution
and runs the full test suite against a `postgres:18-alpine` service container. No secrets are needed
and nothing is pushed.

Pushes to `main` and `v*` tags repeat the build and tests and, only if they pass, also publish
`joeldroid/jnotes` to Docker Hub (`linux/amd64` only) with the tags `latest` and a build version
`{YY}{day of year}{run of the day}` such as `2630501` (both from `main`; the commit gets the same git tag),
`X.Y.Z`/`X.Y`/`X` (from a version tag) and `sha-<short>`. Publishing needs the repository variable
`DOCKERHUB_USERNAME` and the secret `DOCKERHUB_TOKEN` (a Docker Hub access token with Read & Write).
The build version also needs the repository variable `RUN_OF_THE_DAY` (the next run number, starting at `1`;
it is incremented after each tagged build and the first build of a UTC day always uses `1`) and the secret
`VARIABLES_TOKEN`, a fine-grained personal access token limited to this repository with *Variables: read and write*
(the built-in `GITHUB_TOKEN` cannot touch Actions variables).

## Running with Docker

```bash
docker compose up --build
```

A prebuilt image is also available: `docker pull joeldroid/jnotes`.

The app listens on `http://localhost:8080`. Notes and the Data Protection keys are bind-mounted to
`./data` on the host (`./data/notes/<user-id>/*.md`, `./data/keys`) — edit note files there
directly with any editor and they'll be picked up next time the app starts (or via Administration →
Rescan Now while it's running). The **database is a named Docker volume** (`jnotes-pgdata`), not a
file under `./data` — deleting `./data` no longer resets accounts, and `docker compose down -v`
does.

> **Note on file permissions:** the container runs as its base image's non-root user. If Docker
> can't write to `./data` on your host (uncommon, but possible with some Docker Engine/Linux
> setups), fix host-side permissions with `chmod -R a+rwX ./data` or run
> `sudo chown -R $(id -u):$(id -g) ./data` before starting the container.

### PDF export (optional)

The `gotenberg` service in `docker-compose.yml` sits behind a compose profile, so it only runs if you ask for it. To enable PDF export, create a `.env` file next to `docker-compose.yml` with:

```
COMPOSE_PROFILES=pdf
GOTENBERG_URL=http://gotenberg:3000
```

then `docker compose up -d --build`. Gotenberg isn't published to the host; only JNotes talks to it. It runs a headless Chromium, so budget a few hundred MB of RAM. If it's stopped, the button still shows but the new tab explains the PDF service isn't reachable. The PDF is always light-themed, even though the app itself is dark. Mermaid diagrams are rendered in the PDF. Images from other websites appear as links rather than being downloaded.

Running locally instead: `docker run --rm -p 3000:3000 gotenberg/gotenberg:8`, then set `GOTENBERG_URL` (the launch profiles already use `http://localhost:3000`).

### Auto-tagging with Ollama (optional)

A background task sends notes that have **no tags** to a local [Ollama](https://ollama.com) model, which suggests 1–5 tags. It runs only when `OLLAMA_URL` is a valid http(s) URL; there is no separate on/off setting (to turn it off, leave `OLLAMA_URL` unset — the old `AUTO_TAG_ENABLED` variable is ignored, with a startup warning if it is still set). The default model is Microsoft's `phi4` (14B, about 9 GB on disk).

**What gets tagged.**

- Only notes with no tags at all, for every user. It reuses the spelling of tags that user already has, and writes new ones in lowercase kebab-case.
- Only notes left unchanged for 10 minutes, newest first. Existing untagged notes are backfilled gradually: at most `AUTO_TAG_BATCH_SIZE` notes per `AUTO_TAG_INTERVAL_MINUTES` (10 notes every 10 minutes by default).
- Each note gets one attempt. If Ollama answers badly for a note it is retried after 1 h, 6 h, 24 h and 72 h, then given up on. If Ollama itself is down, the pass just stops and tries again on the next interval.
- It never changes tags you've written, and it doesn't change a note's "Last updated" time or its place in the list.
- Notes you edited outside JNotes in a non-standard format (extra front-matter keys, Windows line endings) are skipped, so a background write never rewrites anything but the tags.
- If you remove tags the tagger added, they stay removed.
- An editor left open for more than 10 minutes on an untagged note can overwrite the new tags with its next save.

Administration has a read-only **Auto-tagging** card showing the current state and the last pass. Ollama unloads the model after 5 idle minutes, so the first note of a pass may load it cold, which counts toward `OLLAMA_TIMEOUT_SECONDS`.

**Ollama on the Docker host.** Run `ollama pull phi4`, then create a `.env` file next to `docker-compose.yml` with:

```
OLLAMA_URL=http://host.docker.internal:11434
```

On Linux, Ollama listens on `127.0.0.1` only by default, so set `OLLAMA_HOST=0.0.0.0` for it (or bind it to the Docker bridge address) or the container can't connect. Docker Desktop on Windows and macOS normally reaches services on the host through `host.docker.internal` too.

**Bundled Ollama container.** The `ollama` service in `docker-compose.yml` sits behind a compose profile, like `gotenberg`. In `.env`:

```
COMPOSE_PROFILES=ollama
OLLAMA_URL=http://ollama:11434
```

(use `COMPOSE_PROFILES=pdf,ollama` if you also want PDF export), then `docker compose up -d --build` and pull the model once with `docker compose exec ollama ollama pull phi4`. Budget about 9 GB of disk and enough RAM or VRAM for a 14B model; on CPU only it is slow, so consider raising `OLLAMA_TIMEOUT_SECONDS`. Ollama isn't published to the host, and GPU passthrough isn't configured in the compose file (see the [Ollama Docker docs](https://docs.ollama.com/docker)).

**Running locally.** Start Ollama, then run JNotes with `OLLAMA_URL` set (PowerShell: `$env:OLLAMA_URL="http://localhost:11434"`); the launch profiles set it empty, so nothing is sent anywhere until you set it (set it only against a throwaway database and notes folder).

**Privacy.** Once `OLLAMA_URL` is set, tagging is automatic and covers every user's untagged notes; there is no per-user opt-out, so it is an admin decision. Note text is sent only to `OLLAMA_URL`, and JNotes doesn't log it (or the URL). If that address is a remote or cloud host, your note text leaves your machine. Only the first ~12,000 characters of a long note are sent.

## Authentication

**First run:** with no users yet, every page redirects to a setup screen where you choose the
admin's username and password. That account can then manage everyone else from
**Administration** in the username menu in the header (only visible to admins) — create additional users,
reset a password, or delete an account. Deleting a user never deletes their notes: the folder is
moved to `notes/deleted/<user-id>/` rather than removed, and the account itself is deleted. There's
no "restore" button yet — put the folder back and re-create the account by hand if you need to.

**Each user needs a password, an email (for OIDC), or both** — set when they're created. There's
no self-service password reset or invite email (JNotes has no mail-sending capability); an admin
sets or resets a password directly, and anyone can change their own from **My Account** (click your name in the header).

**OIDC** is optional and configured with the `OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`
env vars (see Configuration above) — once set, a "Sign in with …" button appears on the login page.
Accounts aren't auto-created from OIDC: an admin creates the user first with their email set, and
that account links to the IdP automatically the first time they sign in with a matching email.
Once OIDC is configured, an admin can turn password login off entirely from
Administration → Security (refused if OIDC isn't configured, so you can't lock everyone out).

## Sharing notes

Any user can share any of their own notes with any other user on the instance; there is no admin switch for it.

**How to share.** Open a note and click **Share**. Search for a person by username or name, choose **View only** or **Can edit**, and click **Add**. You can change someone's permission or remove them at any time, and it takes effect on their next action. The note view shows "Shared with N", and a note you've shared shows a "Shared · N" chip in the list.

**What each permission allows.**

| | Owner | Can edit | View only |
| --- | --- | --- | --- |
| Read the note, search it, export it as PDF | yes | yes | yes |
| Edit the body | yes | yes | no |
| Paste or upload images into the body | yes | yes (stored in the owner's folder) | no |
| Change the title or tags | yes | no | no |
| Share it, change or remove people | yes | no | no |
| Move it to Trash, restore, delete it permanently | yes | no | no |
| **Leave** the share | n/a | yes | yes |

A shared note appears in the recipient's **All Notes** and search results with a "Shared by …" chip, and on the **Shared with Me** page. A recipient can **Leave** a share from the note view; the owner can share it again.

**Trash and deleting.** When the owner moves a shared note to Trash it disappears for the people it's shared with (their list, and any open page, report it as not found). Restoring it brings everyone back with the same permissions. Deleting it permanently from Trash removes the shares. Deleting a user account removes the shares they gave and received, but never their notes (see [Authentication](#authentication)).

**Where things live.** Sharing changes nothing on disk: a shared note stays in its owner's folder (`notes/<owner-id>/<note-id>.md`, images under `notes/<owner-id>/images/`), and nothing is copied. Who has access is stored in the PostgreSQL `NoteShares` table, not in the note's front matter, so it is not part of the portable files and is lost with the database (for example by `docker compose down -v`). The note's file format is unchanged.

**Edits by more than one person.** Saves are last-writer-wins, as with two browser tabs. An editor's save writes only the body and keeps the owner's title and tags, so a rename or tag change made in the meantime isn't undone. If access is removed while someone has the note open in the editor, their next save is refused with a message that the change was not saved.

**Privacy.** To pick a recipient, every signed-in user can search all accounts by username, first name and last name (email addresses are never shown or searched). Anyone with access to a note can see its images and, for a Can edit recipient, add images to the owner's folder.

## Note file format

Each note is `notes/<user-id>/<note-id>.md`, one markdown file per note, per user:

```markdown
---
id: 3fa85f64-5717-4562-b3fc-2c963f66afa6
title: Grocery List
type: text
tags: [personal, shopping]
created: 2026-09-12T10:15:00Z
updated: 2026-09-12T11:02:00Z
---
Pick up milk and eggs on the way home.

- [ ] buy milk
- [ ] buy eggs
```

`type` is always `text` (kept for compatibility; older files with `type: checklist` open as ordinary text notes). Timestamps are UTC. The `id` is duplicated from the filename
inside the front matter so a renamed/copied file can still be recognized correctly on rescan.

A diagram in a note is just a ` ```mermaid ` fenced code block in the body, so the file stays
ordinary markdown that GitHub, Obsidian, and similar tools also render.

An image inserted into a note is saved as `notes/<user-id>/images/<random-id>.<ext>` and linked
from the body as `![name](images/<random-id>.<ext>)` — relative to the note, so it resolves in any
editor pointed at the folder. Deleting a note (or the link) doesn't delete its image file, since the same image can be used by several notes; JNotes removes the ones no note (or trashed note) uses any more, for all users, every 7 days by default. An admin can change the interval in days, or set it to 0 to turn it off, under **Administration → Images**, and can also run it immediately with **Clean up now**. The first automatic pass runs one interval after the feature is first started, not at startup.

Sharing a note adds nothing to the front matter (see [Sharing notes](#sharing-notes)).
