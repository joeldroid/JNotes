# Running locally

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
Reconciliation also runs on a timer (`AUTO_INDEX_INTERVAL_MINUTES`, see [Configuration](configuration.md)), so externally
edited/added/removed `.md` files are picked up without a restart. The accounts/settings tables
beside it are **not** rebuildable and do need a backup — see the `pg_dump` callout in [Configuration](configuration.md).

On first run with no users in the database, every request redirects to a setup screen where you
pick the admin's username and password — see [Authentication](authentication.md).
