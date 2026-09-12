# JNotes

A self-hosted, single-user note-taking app built with Blazor Server (.NET 10). Notes are stored
as portable markdown files on disk — copy the folder and every note is a readable `.md` file in
any editor. A SQLite database indexes the notes for fast search/listing and stores app settings;
it's a derived cache, never the source of truth.

## Features

- **Text notes** — free-form markdown, edited with a live preview.
- **Checklist notes** — markdown task lists (`- [ ] item`) rendered as interactive checkboxes.
- **Portable storage** — every note is `notes/<id>.md` with a small YAML front matter header
  (title, tags, type, timestamps) followed by the markdown body.
- **Responsive UI** — Bootstrap 5, usable on both desktop and mobile.
- **Self-healing index** — the SQLite index is reconciled against the notes folder at startup and
  on demand (Settings → Rescan Now), so edits made directly to the files (outside the app, or
  while the container was stopped) are picked up automatically.

## Project layout

```
JNotes.slnx
src/
  JNotes.Web/     Blazor Server UI + composition root (Program.cs)
  JNotes.Core/     Domain models, markdown/front-matter serialization, file storage, EF Core/SQLite indexing, settings
tests/
  JNotes.Tests/    xUnit tests for JNotes.Core
```

`JNotes.Web` never touches the filesystem or EF Core/SQLite directly — it only consumes
`INoteService`, `ISettingsService`, and `INoteIndexReconciler` from `JNotes.Core`. This is
enforced at build time via `Microsoft.CodeAnalysis.BannedApiAnalyzers`
(`src/JNotes.Web/BannedSymbols.txt`).

### Styling

JNotes's own CSS is written as Sass modules under `src/JNotes.Web/Styles/` — `_variables.scss`,
`_blazor-template.scss` (framework boilerplate), `_note-editor.scss` (the editor/preview styles,
the module that'll grow with the app), all pulled together by the `app.scss` entry point. Add a
new concern as its own `_*.scss` partial and `@use` it from `app.scss`.

[`AspNetCore.SassCompiler`](https://github.com/koenvzeijl/AspNetCore.SassCompiler) compiles this
to `wwwroot/css/app.css` automatically as part of `dotnet build`/`dotnet publish` (no Node.js
needed — it bundles a dart-sass binary) — the compiled output is gitignored, and
`dotnet watch --project src/JNotes.Web run` live-recompiles on save. Config is in
`src/JNotes.Web/sasscompiler.json` (expanded output in Debug, compressed in Release/Docker).

Bootstrap itself stays as the vendored precompiled CSS/JS in `wwwroot/lib/bootstrap` — it isn't
part of the Sass build. Compiling Bootstrap from its own Sass source (to override its variables
for a custom theme) is possible later but is a separate, larger change.

## Running locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/JNotes.Web
# or, for auto-rebuild on change:
dotnet watch --project src/JNotes.Web run
```

By default this stores notes and the SQLite database under `src/JNotes.Web/App_Data/` (see the
`NOTES_PATH`/`DB_PATH` keys in `appsettings.json`) — gitignored, safe to delete to start fresh.
Override either with an environment variable of the same name (`NOTES_PATH`, `DB_PATH`) — this is
how `docker-compose.yml` points them at `/data`.

### Configuration

| Env var / config key | Default | Purpose |
| --- | --- | --- |
| `NOTES_PATH` | `App_Data/notes` (local) / `/data/notes` (Docker) | Folder note markdown files are read from/written to. |
| `DB_PATH` | `App_Data/jnotes.db` (local) / `/data/jnotes.db` (Docker) | SQLite index/settings database file. |
| `TZ` | `UTC` | IANA time zone id (e.g. `America/New_York`, `Europe/London`) note timestamps are *displayed* in. Storage is always UTC regardless — this only affects what you see in the UI. |

All three take effect at startup only — changing one requires an app/container restart.

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
on the host (`./data/notes/*.md`, `./data/jnotes.db`) — edit files there directly with any editor
and they'll be picked up next time the app starts (or via Settings → Rescan Now while it's
running).

> **Note on file permissions:** the container runs as its base image's non-root user. If Docker
> can't write to `./data` on your host (uncommon, but possible with some Docker Engine/Linux
> setups), fix host-side permissions with `chmod -R a+rwX ./data` or run
> `sudo chown -R $(id -u):$(id -g) ./data` before starting the container.

## Note file format

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
