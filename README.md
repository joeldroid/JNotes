# JNotes

A self-hosted, multi-user note-taking app built with Blazor Server (.NET 10). Notes are stored
as portable markdown files on disk — copy the folder and every note is a readable `.md` file in
any editor. A Redis-backed index makes the notes fast to search/list, and a SQLite database
stores app settings and user accounts; the index is a derived cache, never the source of truth.

The very first time you run JNotes, you'll land on a setup screen to create the admin account —
see [Authentication](#authentication) below for that flow, adding more users, and OIDC.

## Features

- **Text notes** — free-form markdown, edited with a live preview, with `mermaid` code blocks rendered as diagrams (toolbar buttons insert a starter flow chart), and images inserted with the **Image** button or by pasting one (PNG, JPEG, GIF or WebP, up to 10 MB).
- **Checklist notes** — markdown task lists (`- [ ] item`) rendered as interactive checkboxes. Add items and group them into categories (markdown `##` headings) from the note view or the editor without touching markdown; checking an item moves it below the unchecked ones in its category.
- **Multi-user with real accounts** — admin-created users, each with their own notes and settings;
  sign in with a password, OIDC, or both.
- **Portable storage** — every note is `notes/<user-id>/<id>.md` with a small YAML front matter
  header (title, tags, type, timestamps) followed by the markdown body.
- **Responsive UI** — Bootstrap 5, usable on both desktop and mobile.
- **Self-healing index** — the Redis index is reconciled against the notes folder at startup and
  on demand (Settings → Rescan Now), so edits made directly to the files (outside the app, or
  while the container was stopped) are picked up automatically.

## Project layout

```
JNotes.slnx
src/
  JNotes.Web/     Blazor Server UI + composition root (Program.cs)
  JNotes.Core/     Domain models, markdown/front-matter serialization, file storage, Redis note indexing, EF Core/SQLite settings
tests/
  JNotes.Tests/    xUnit tests for JNotes.Core
```

`JNotes.Web` never touches the filesystem, EF Core/SQLite, or Redis directly — it only consumes
`INoteService`, `ISettingsService`, `INoteIndexReconciler`, `IUserRepository`,
`IUserManagementService`, and `IImageService` from `JNotes.Core`. This is enforced at build time via
`Microsoft.CodeAnalysis.BannedApiAnalyzers` (`src/JNotes.Web/BannedSymbols.txt`).

### Styling

JNotes's own CSS is plain, modern CSS — hand-authored files served directly from
`src/JNotes.Web/wwwroot/css/`, no build step, no Node.js, no CSS preprocessor. `variables.css`
defines app-wide custom properties (`:root { --note-editor-height: 60vh; ... }`); `note-editor.css`,
`note-list.css`, and `nav-menu.css` are one file per concern, using native CSS nesting (`&`) instead
of a Sass equivalent; `app.css` pulls them all together with native `@import`. Add a new concern as
its own `.css` file and `@import` it from `app.css`.

Bootstrap itself stays as the vendored precompiled CSS/JS in `wwwroot/lib/bootstrap` — it isn't
part of JNotes's own CSS. Compiling Bootstrap from its own Sass source (to override its variables
for a custom theme) is possible later but is a separate, larger change.

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

By default this stores notes and the SQLite database (settings and user accounts, not the note
index — see below) under `src/JNotes.Web/App_Data/` (see the `NOTES_PATH`/`DB_PATH` keys in
`appsettings.json`) — gitignored, safe to delete to start fresh. Override either with an
environment variable of the same name (`NOTES_PATH`, `DB_PATH`) — this is how `docker-compose.yml`
points them at `/data`.

The note index (everything `NOTES_PATH`'s files are indexed into for listing/search) lives in
Redis, not SQLite — set `REDIS_CONNECTION_STRING` to a reachable Redis instance (locally, e.g.
`docker run -p 6379:6379 redis:8-alpine`, then the `appsettings.json` default of `localhost:6379`
just works). The index is a rebuildable cache: if Redis is empty (fresh container, flushed data),
the startup reconciliation pass rebuilds it from the markdown files, so it needs no backup of its
own. Reconciliation also runs on a timer (`AUTO_INDEX_INTERVAL_MINUTES`, see below), so
externally edited/added/removed `.md` files are picked up without a restart.

On first run with no users in the database, every request redirects to a setup screen where you
pick the admin's username and password — see [Authentication](#authentication) below.

> **Upgrading from an older version of JNotes that predates real authentication?** There's no
> automated migration, and — unlike earlier upgrades — this one leaves the app **completely
> unusable** if you skip the reset: existing accounts have no password and no admin role, so
> there's no way to sign in or reach `/settings` to fix it. Delete `App_Data` (or your
> `NOTES_PATH`/`DB_PATH` targets) and start fresh; you'll go through the setup screen again. If you
> want to keep old notes, move each `notes/<id>.md` file by hand into a `notes/<user-id>/`
> subfolder for whichever user should own it (find the new user's id in `/settings`'s
> Administration settings section, or the `Users` table in `jnotes.db`) before starting the app.

### Configuration

| Env var / config key | Default | Purpose |
| --- | --- | --- |
| `NOTES_PATH` | `App_Data/notes` (local) / `/data/notes` (Docker) | Folder note markdown files are read from/written to. |
| `DB_PATH` | `App_Data/jnotes.db` (local) / `/data/jnotes.db` (Docker) | SQLite database file (settings and user accounts — not the note index, see `REDIS_CONNECTION_STRING`). |
| `REDIS_CONNECTION_STRING` | `localhost:6379` (local) / `redis:6379` (Docker) | StackExchange.Redis connection string backing the note index. Required — there's no SQLite fallback. |
| `TZ` | `UTC` | IANA time zone id (e.g. `America/New_York`, `Europe/London`) note timestamps are *displayed* in. Storage is always UTC regardless — this only affects what you see in the UI. |
| `OIDC_AUTHORITY` | unset | Your OIDC provider's issuer URL. Leave unset to disable OIDC entirely. |
| `OIDC_CLIENT_ID` | unset | OIDC client id registered with your provider. |
| `OIDC_CLIENT_SECRET` | unset | OIDC client secret. All three `OIDC_*` values must be set for OIDC to be offered. |
| `OIDC_DISPLAY_NAME` | `OIDC` | Label on the "Sign in with …" button on the login page. |
| `AUTO_INDEX_INTERVAL_MINUTES` | `5` | How often the background pass reconciles every user's note index against the files on disk. `0` disables it (the startup pass and Settings → "Rescan Now" still work). |

All of these take effect at startup only — changing one requires an app/container restart.

> **Backing up `jnotes.db`?** It runs in SQLite's WAL journal mode, so two more files sit next to
> it — `jnotes.db-wal` and `jnotes.db-shm`. A plain copy of just `jnotes.db` mid-run can miss data
> still sitting in the WAL file. Stop the app before copying, or checkpoint first
> (`sqlite3 jnotes.db "PRAGMA wal_checkpoint(TRUNCATE);"`) and copy all three files together. This
> only affects the settings/accounts database — your notes themselves are always plain `.md`
> files, safe to copy anytime, and the Redis index is rebuildable regardless.

### Tests

```bash
dotnet test JNotes.slnx
# a single test:
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"
```

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

The app listens on `http://localhost:8080`. Notes and the database are bind-mounted to `./data`
on the host (`./data/notes/<user-id>/*.md`, `./data/jnotes.db`) — edit files there directly with
any editor and they'll be picked up next time the app starts (or via Settings → Rescan Now while
it's running). As with running locally, upgrading from a pre-multi-user version means deleting
`./data` and starting fresh (see the note above) — there's no automated migration.

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
editor pointed at the folder. Deleting a note (or the link) doesn't delete its image file.

JNotes keeps checked items after unchecked ones within each run of adjacent checklist items, but the
file is still ordinary markdown — you can reorder it by hand.
