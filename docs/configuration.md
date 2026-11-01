# Configuration

All configuration is environment variables only — there is no `appsettings.json`. See [Running locally](running-locally.md) for how the launch profiles and Docker supply them.

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
| `OLLAMA_URL` | No | unset (auto-tagging off). Set to empty in the launch profiles | Base URL of an [Ollama](https://ollama.com) server (e.g. `http://localhost:11434`); enables background auto-tagging. Must be an absolute http(s) URL, otherwise a warning is logged and it stays off; every user's untagged note text is sent here. See [Auto-tagging with Ollama](auto-tagging.md). |
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
[Development](development.md#tests)), are read only by the test suites, never by the app.

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
