# Sharing notes

Any user can share any of their own notes with any other user on the instance; there is no admin switch for it.

**How to share.** Open a note and click **Share**. Search for a person by username or name, choose **View only** or **Can edit**, and click **Add**. You can change someone's permission or remove them at any time, and it takes effect on their next action. The note view shows "Shared with N", and a note you've shared shows a "Shared · N" chip in the list.

**What each permission allows.**

| | Owner | Can edit | View only |
| --- | --- | --- | --- |
| Read the note, search it, export it as PDF | yes | yes | yes |
| Edit the body | yes | yes | no |
| Paste or upload images into the body | yes | yes (stored in the owner's folder) | no |
| Change the title or tags | yes | no | no |
| Share it, change or remove people | yes | no | no |
| Move it to Trash, restore, delete it permanently | yes | no | no |
| **Leave** the share | n/a | yes | yes |

A shared note appears in the recipient's **All Notes** and search results with a "Shared by …" chip, and on the **Shared with Me** page. A recipient can **Leave** a share from the note view; the owner can share it again.

**Trash and deleting.** When the owner moves a shared note to Trash it disappears for the people it's shared with (their list, and any open page, report it as not found). Restoring it brings everyone back with the same permissions. Deleting it permanently from Trash removes the shares. Deleting a user account removes the shares they gave and received, but never their notes (see [Authentication](authentication.md)).

**Where things live.** Sharing changes nothing on disk: a shared note stays in its owner's folder (`notes/<owner-id>/<note-id>.md`, images under `notes/<owner-id>/images/`), and nothing is copied. Who has access is stored in the PostgreSQL `NoteShares` table, not in the note's front matter, so it is not part of the portable files and is lost with the database (for example by `docker compose down -v`). The note's file format is unchanged.

**Edits by more than one person.** Saves are last-writer-wins, as with two browser tabs. An editor's save writes only the body and keeps the owner's title and tags, so a rename or tag change made in the meantime isn't undone. If access is removed while someone has the note open in the editor, their next save is refused with a message that the change was not saved.

**Privacy.** To pick a recipient, every signed-in user can search all accounts by username, first name and last name (email addresses are never shown or searched). Anyone with access to a note can see its images and, for a Can edit recipient, add images to the owner's folder.
