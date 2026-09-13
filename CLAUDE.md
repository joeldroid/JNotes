# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JNotes is a self-hosted, single-user note-taking app: Blazor Server (.NET 10) UI, notes stored as
portable markdown files on disk (each a readable `.md` with YAML front matter), and a SQLite
database that indexes them for fast search — the index is a **derived cache**, never the source
of truth. See README.md for features, the note file format, and full configuration reference.

## Commands

```bash
# Build / test / format (from repo root)
dotnet build JNotes.slnx
dotnet test JNotes.slnx
dotnet test tests/JNotes.Tests --filter "FullyQualifiedName~ChecklistMarkdownParserTests"   # single test/class
dotnet format JNotes.slnx
dotnet format JNotes.slnx --verify-no-changes   # CI-style check, no changes written

# Run locally (needs .NET 10 SDK; stores data under src/JNotes.Web/App_Data/, gitignored)
dotnet run --project src/JNotes.Web
dotnet watch --project src/JNotes.Web run       # live-reloads C#, Razor, and Styles/*.scss

# EF Core migrations — startup-project is Core, NOT Web: JNotes.Web deliberately has no
# EF Core Design package reference (see Architecture below), so Web can't be the startup project.
dotnet ef migrations add <Name> --project src/JNotes.Core --startup-project src/JNotes.Core -o Data/Migrations
dotnet ef database update --project src/JNotes.Core --startup-project src/JNotes.Core
# `dotnet ef` is a local tool (.config/dotnet-tools.json) — run `dotnet tool restore` once after cloning.

# Docker (production-shaped: this is what Docker actually runs, not `dotnet run`)
docker compose up --build
```

## Architecture

**Three projects**, enforced one-directional dependency:
`JNotes.Web` (Blazor Server UI + composition root) → `JNotes.Core` (domain, storage, indexing,
EF Core) ← `JNotes.Tests` (xUnit, tests Core only).

**Web never touches the filesystem or EF Core/SQLite directly.** It only consumes three
interfaces from Core: `INoteService`, `ISettingsService`, `INoteIndexReconciler`. This is
enforced at *build time*, not just by convention: `Microsoft.CodeAnalysis.BannedApiAnalyzers`
bans `System.IO.File`/`Directory`, `DbContext`, and `SqliteConnection` in `JNotes.Web`
(`src/JNotes.Web/BannedSymbols.txt`) — a real build error, not a warning, if violated. All of
Core's DI wiring lives in one place: `JNotes.Core/ServiceCollectionExtensions.AddJNotesCore`.

**Files are the source of truth; SQLite is a derived index.** This shapes `NoteService`
(`JNotes.Core/Notes/NoteService.cs`) and `NoteIndexReconciler`
(`JNotes.Core/Indexing/NoteIndexReconciler.cs`):
- Every write goes **file first, then index** (`NoteService`) — a crash mid-operation leaves an
  orphaned file the next reconciliation will discover and index, never an index row pointing at a
  file that was never written. Delete is file-first too, for the symmetric reason.
- `NoteIndexReconciler.RescanAsync()` diffs the notes folder against the index (added / updated —
  by comparing on-disk mtime vs. the last-indexed mtime, so unchanged files aren't re-parsed /
  removed / corrupted-front-matter-errors-recorded-without-deleting-the-row) and runs once at
  startup plus on demand from Settings → "Rescan Now". This is what makes hand-edited or
  externally-added/removed `.md` files converge correctly. There's deliberately no
  `FileSystemWatcher` — unreliable across Docker bind-mount backends.
- `ListAsync`/search reads only the SQLite index (fast, never touches disk); only `GetAsync`
  (opening one note) reads the actual file.

**Checklist toggling is line-based regex, not a Markdig AST round-trip**
(`JNotes.Core/Serialization/ChecklistMarkdownParser.cs`). Markdig is used only for read-only
preview rendering; re-emitting markdown from its parsed tree risks reformatting content outside
the edited line, so toggling a checkbox mutates only that line's bracket character in place,
leaving the rest of the file byte-identical. Don't route checklist edits through Markdig's AST.

**Front matter is hand-split, not Markdig-parsed**
(`JNotes.Core/Serialization/FrontMatterSplitter.cs`): a "does the file start with a `---`-delimited
block" string search, then YamlDotNet deserializes the YAML. `NoteMarkdownSerializer` maps
between that and the domain `Note` model (`Models/Note.cs`); timestamps are UTC everywhere in
storage (`JNotesOptions.TimeZoneId`/`AppTimeZoneProvider` in `Core/Time/` only affects *display*
in the Web UI, via the `TZ` env var).

**Tests** (`tests/JNotes.Tests/`) split into pure unit tests (`Serialization/`, `Time/`) and
integration tests (`Integration/`) that spin up the real `AddJNotesCore` DI graph against a real
temp-file SQLite database and temp folder (`CoreServicesFixture`) — not the EF Core in-memory
provider, specifically so SQLite-specific behavior (like the `ORDER BY DateTimeOffset` limitation
worked around client-side in `EfNoteIndexRepository.SearchAsync`) is actually exercised.

**Docker build gotcha:** the Dockerfile's multi-stage build restores against just the `.csproj`
files first (for layer caching) then copies full source — the publish step must **not** pass
`--no-restore`, or the static web assets manifest gets computed from that incomplete early
restore and silently ships without `_framework/blazor.web.js`, breaking all client-side
interactivity in the built image while every `curl`-based check still looks fine (no
`Accept-Encoding` header, so compressed-asset issues don't show either). If touching the
Dockerfile, verify with a real browser (or at minimum `curl -H "Accept-Encoding: gzip"`) against
the built image, not just a plain `curl` health check.
