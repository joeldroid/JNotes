# Note file format

Each note is `notes/<user-id>/<note-id>.md`, one markdown file per note, per user:

```markdown
---
id: 3fa85f64-5717-4562-b3fc-2c963f66afa6
title: Grocery List
type: text
tags: [personal, shopping]
created: 2026-09-12T10:15:00Z
updated: 2026-09-12T11:02:00Z
---
Pick up milk and eggs on the way home.

- [ ] buy milk
- [ ] buy eggs
```

`type` is always `text` (kept for compatibility; older files with `type: checklist` open as ordinary text notes). Timestamps are UTC. The `id` is duplicated from the filename
inside the front matter so a renamed/copied file can still be recognized correctly on rescan.

A diagram in a note is just a ` ```mermaid ` fenced code block in the body, so the file stays
ordinary markdown that GitHub, Obsidian, and similar tools also render.

An image inserted into a note is saved as `notes/<user-id>/images/<random-id>.<ext>` and linked
from the body as `![name](images/<random-id>.<ext>)` — relative to the note, so it resolves in any
editor pointed at the folder. Deleting a note (or the link) doesn't delete its image file, since the same image can be used by several notes; JNotes removes the ones no note (or trashed note) uses any more, for all users, every 7 days by default. An admin can change the interval in days, or set it to 0 to turn it off, under **Administration → Images**, and can also run it immediately with **Clean up now**. The first automatic pass runs one interval after the feature is first started, not at startup.

Sharing a note adds nothing to the front matter (see [Sharing notes](sharing.md)).
