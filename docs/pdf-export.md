# PDF export (optional)

The `gotenberg` service in `docker-compose.yml` sits behind a compose profile, so it only runs if you ask for it. To enable PDF export, create a `.env` file next to `docker-compose.yml` with:

```
COMPOSE_PROFILES=pdf
GOTENBERG_URL=http://gotenberg:3000
```

then `docker compose up -d --build`. Gotenberg isn't published to the host; only JNotes talks to it. It runs a headless Chromium, so budget a few hundred MB of RAM. If it's stopped, the button still shows but the new tab explains the PDF service isn't reachable. The PDF is always light-themed, even though the app itself is dark. Mermaid diagrams are rendered in the PDF. Images from other websites appear as links rather than being downloaded.

Running locally instead: `docker run --rm -p 3000:3000 gotenberg/gotenberg:8`, then set `GOTENBERG_URL` (the launch profiles already use `http://localhost:3000`).
