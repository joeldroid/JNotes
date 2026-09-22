# JNotes

A self-hosted, multi-user note-taking app built with Blazor Server (.NET 10). Notes are stored
as portable markdown files on disk — copy the folder and every note is a readable `.md` file in
any editor. A single PostgreSQL database holds user accounts and settings, and the note index that
makes notes fast to search/list; the index tables are a derived cache, never the source of truth —
the markdown files always are.

The very first time you run JNotes, you'll land on a setup screen to create the admin account —
see [Authentication](#authentication) below for that flow, adding more users, and OIDC.

## Features

- **Text notes** — free-form markdown, edited with a live preview, with `mermaid` code blocks rendered as diagrams (toolbar buttons insert a starter flow chart), and images inserted with the **Image** button or by pasting one (PNG, JPEG, GIF or WebP, up to 10 MB).
- **Checklist notes** — markdown task lists (`- [ ] item`) rendered as interactive checkboxes. Add items and group them into categories (markdown `##` headings) from the note view or the editor without touching markdown; checking an item moves it below the unchecked ones in its category.
- **Multi-user with real accounts** — admin-created users, each with their own notes and settings;
  sign in with a password, OIDC, or both.
- **Portable storage** — every note is `notes/<user-id>/<id>.md` with a small YAML front matter
  header (title, tags, type, timestamps) followed by the markdown body.
- **Responsive UI** — [MudBlazor](https://mudblazor.com) (Material design) with a light/dark theme, usable on both desktop and mobile.
- **Self-healing index** — the note index is reconciled against the notes folder at startup and
  on demand (Settings → Rescan Now), so edits made directly to the files (outside the app, or
  while the container was stopped) are picked up automatically.

## Project layout

```
JNotes.slnx
src/
  JNotes.Web/     Blazor Server UI + composition root (Program.cs)
  JNotes.Core/     Domain models, markdown/front-matter serialization, file storage, note indexing, EF Core/PostgreSQL
tests/
  JNotes.Tests/    xUnit tests for JNotes.Core
```

`JNotes.Web` never touches the filesystem or EF Core/PostgreSQL directly — it only consumes
`INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`,
`IUserManagementService`, and `IImageService` from `JNotes.Core`. This is enforced at build time via
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

By default this stores notes under `src/JNotes.Web/App_Data/` (see the `NOTES_PATH` key in
`appsettings.json`) — gitignored, safe to delete to start fresh. Override it with an environment
variable of the same name (`NOTES_PATH`) — this is how `docker-compose.yml` points it at `/data`.

**PostgreSQL is required to run at all** — it holds user accounts, settings, and the note index
(everything `NOTES_PATH`'s files are indexed into for listing/search). Locally, e.g.:

```bash
docker run --rm -p 5432:5432 -e POSTGRES_USER=jnotes -e POSTGRES_PASSWORD=jnotes -e POSTGRES_DB=jnotes postgres:18-alpine
```

then `appsettings.json`'s default `POSTGRES_CONNECTION_STRING` just works. The index (the
`NoteIndex*` tables) is a rebuildable cache: if it's empty (fresh database, truncated tables), the
startup reconciliation pass rebuilds it from the markdown files, so it needs no backup of its own.
Reconciliation also runs on a timer (`AUTO_INDEX_INTERVAL_MINUTES`, see below), so externally
edited/added/removed `.md` files are picked up without a restart. The accounts/settings tables
beside it are **not** rebuildable and do need a backup — see the `pg_dump` callout below.

On first run with no users in the database, every request redirects to a setup screen where you
pick the admin's username and password — see [Authentication](#authentication) below.

> **Upgrading from a SQLite/Redis version of JNotes?** There is no automated migration. Your notes
> are untouched (they're files), but user accounts and settings do not carry over: point
> `POSTGRES_CONNECTION_STRING` at an empty database and you'll go through the `/setup` screen
> again. The re-created user gets a **new** user id, so rename `notes/<old-user-id>/` to
> `notes/<new-user-id>/` (find the new id in Settings → Administration settings) to get the old
> notes back, then Settings → "Rescan Now". You can delete the old `jnotes.db`/`-wal`/`-shm` files
> and stop your Redis container.

### Configuration

| Env var / config key | Default | Purpose |
| --- | --- | --- |
| `NOTES_PATH` | `App_Data/notes` (local) / `/data/notes` (Docker) | Folder note markdown files are read from/written to. |
| `POSTGRES_CONNECTION_STRING` | `Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes` (local) / the compose `postgres` service (Docker) | Npgsql connection string for the one database holding user accounts, settings and the note index. Required — there's no fallback, and a missing or unreachable value fails startup rather than starting degraded. |
| `DATA_PROTECTION_KEYS_PATH` | `App_Data/keys` (local) / `/data/keys` (Docker) | Where the keys that sign the auth cookie are stored. Must be on persistent storage — on a container's ephemeral filesystem every restart regenerates them and silently signs everyone out. Defaults to a `keys` folder beside `NOTES_PATH`. |
| `TZ` | `UTC` | IANA time zone id (e.g. `America/New_York`, `Europe/London`) note timestamps are *displayed* in. Storage is always UTC regardless — this only affects what you see in the UI. |
| `OIDC_AUTHORITY` | unset | Your OIDC provider's issuer URL. Leave unset to disable OIDC entirely. |
| `OIDC_CLIENT_ID` | unset | OIDC client id registered with your provider. |
| `OIDC_CLIENT_SECRET` | unset | OIDC client secret. All three `OIDC_*` values must be set for OIDC to be offered. |
| `OIDC_DISPLAY_NAME` | `OIDC` | Label on the "Sign in with …" button on the login page. |
| `AUTO_INDEX_INTERVAL_MINUTES` | `5` | How often the background pass reconciles every user's note index against the files on disk. `0` disables it (the startup pass and Settings → "Rescan Now" still work). |

All of these take effect at startup only — changing one requires an app/container restart.

> **What are the Data Protection keys, and why do they need their own path?** ASP.NET Core signs the
> auth cookie with them — without persisting them somewhere durable, every restart generates a fresh
> set and silently signs everyone out (nothing errors; sessions just stop working). They used to be
> derived from `DB_PATH`'s directory, back when the database was a local SQLite file; a PostgreSQL
> connection string doesn't point at a local path, so they got their own setting,
> `DATA_PROTECTION_KEYS_PATH`. Left unset, it falls back to a `keys` folder beside `NOTES_PATH`,
> reproducing the old default location either way — just make sure wherever it points is on
> persistent storage, not a container's ephemeral filesystem.

> **Backing up the database?** Use `pg_dump` (e.g.
> `docker compose exec postgres pg_dump -U jnotes jnotes > jnotes.sql`), not a plain file copy —
> copying the volume's files while the server is running is not a backup. Your notes themselves are
> always plain `.md` files, safe to copy anytime, and the `NoteIndex*` tables in that dump are
> rebuildable anyway.

### Tests

```bash
dotnet test JNotes.slnx
# a single test:
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"
```

The integration and `Users` suites need a **real PostgreSQL** (same `docker run` line as above;
override with `JNOTES_TEST_POSTGRES_CONNECTION_STRING`). Each test class creates and drops its own
`jnotes_test_<guid>` database, so the configured user needs `CREATEDB` (the official image's
`POSTGRES_USER` is a superuser, so the documented `docker run` just works).

### Other commands

```bash
dotnet build JNotes.slnx
dotnet format JNotes.slnx
dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
dotnet ef database update --project src/JNotes.Core --startup-project src/JNotes.Core
```

`dotnet ef` is installed as a local tool (`.config/dotnet-tools.json`) — run `dotnet tool restore`
once after cloning if `dotnet ef` isn't found.

## Running with Docker

```bash
docker compose up --build
```

The app listens on `http://localhost:8080`. Notes and the Data Protection keys are bind-mounted to
`./data` on the host (`./data/notes/<user-id>/*.md`, `./data/keys`) — edit note files there
directly with any editor and they'll be picked up next time the app starts (or via Settings →
Rescan Now while it's running). The **database is a named Docker volume** (`jnotes-pgdata`), not a
file under `./data` — deleting `./data` no longer resets accounts, and `docker compose down -v`
does. As with running locally, upgrading from a SQLite/Redis version means starting fresh (see the
note above) — there's no automated migration.

> **Note on file permissions:** the container runs as its base image's non-root user. If Docker
> can't write to `./data` on your host (uncommon, but possible with some Docker Engine/Linux
> setups), fix host-side permissions with `chmod -R a+rwX ./data` or run
> `sudo chown -R $(id -u):$(id -g) ./data` before starting the container.

## Authentication

**First run:** with no users yet, every page redirects to a setup screen where you choose the
admin's username and password. That account can then manage everyone else from Settings →
**Administration settings** (only visible once signed in as an admin) — create additional users,
reset a password, or delete an account. Deleting a user never deletes their notes: the folder is
moved to `notes/deleted/<user-id>/` rather than removed, and the account itself is deleted. There's
no "restore" button yet — put the folder back and re-create the account by hand if you need to.

**Each user needs a password, an email (for OIDC), or both** — set when they're created. There's
no self-service password reset or invite email (JNotes has no mail-sending capability); an admin
sets or resets a password directly, and anyone can change their own from Settings → Account.

**OIDC** is optional and configured with the `OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`
env vars (see Configuration above) — once set, a "Sign in with …" button appears on the login page.
Accounts aren't auto-created from OIDC: an admin creates the user first with their email set, and
that account links to the IdP automatically the first time they sign in with a matching email.
Once OIDC is configured, an admin can turn password login off entirely from Settings →
Administration settings → Security (refused if OIDC isn't configured, so you can't lock everyone out).

## Note file format

Each note is `notes/<user-id>/<note-id>.md`, one markdown file per note, per user:

```markdown
---
id: 3fa85f64-5717-4562-b3fc-2c963f66afa6
title: Grocery List
type: checklist
tags: [personal, shopping]
created: 2026-09-12T10:15:00Z
updated: 2026-09-12T11:02:00Z
---
- [ ] buy milk
- [ ] buy eggs
```

`type` is `text` or `checklist`. Timestamps are UTC. The `id` is duplicated from the filename
inside the front matter so a renamed/copied file can still be recognized correctly on rescan.

A diagram in a text note is just a ` ```mermaid ` fenced code block in the body, so the file stays
ordinary markdown that GitHub, Obsidian, and similar tools also render.

An image inserted into a text note is saved as `notes/<user-id>/images/<random-id>.<ext>` and linked
from the body as `![name](images/<random-id>.<ext>)` — relative to the note, so it resolves in any
editor pointed at the folder. Deleting a note (or the link) doesn't delete its image file, since the same image can be used by several notes; JNotes removes the ones no note (or trashed note) uses any more, for all users, every 7 days by default. An admin can change the interval in days, or set it to 0 to turn it off, under **Settings → Administration → Images**, and can also run it immediately with **Clean up now**. The first automatic pass runs one interval after the feature is first started, not at startup.

JNotes keeps checked items after unchecked ones within each run of adjacent checklist items, but the
file is still ordinary markdown — you can reorder it by hand.
