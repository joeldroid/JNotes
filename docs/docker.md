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

> **Note on file permissions:** the container runs as its base image's non-root user (UID `1654`),
> and a bind mount replaces the `/data` ownership the image sets up. If `./data` doesn't exist yet,
> Docker Engine on Linux creates it owned by root, and the app fails with
> `Access to the path '/data/keys' is denied`. Give UID 1654 ownership before starting the container:
> `mkdir -p ./data && sudo chown -R 1654:1654 ./data` (or, less strictly, `chmod -R a+rwX ./data`).
> `chown` to your own user (`$(id -u)`) only helps if that happens to be UID 1654.

## Optional services

- [PDF export](pdf-export.md) — adds a Gotenberg container.
- [Auto-tagging with Ollama](auto-tagging.md) — adds (or points at) an Ollama server.
