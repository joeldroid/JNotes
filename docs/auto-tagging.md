# Auto-tagging with Ollama (optional)

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
