# Running with Docker

```bash
docker compose up --build
```

Prebuilt images are also available from Docker Hub (`docker pull joeldroid/jnotes`) and the GitHub Container Registry (`docker pull ghcr.io/joeldroid/jnotes`); see the [README quick start](../README.md#quick-start) for using one in the compose file.

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

## Optional services

- [PDF export](pdf-export.md) — adds a Gotenberg container.
- [Auto-tagging with Ollama](auto-tagging.md) — adds (or points at) an Ollama server.
