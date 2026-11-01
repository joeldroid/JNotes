# Authentication

**First run:** with no users yet, every page redirects to a setup screen where you choose the
admin's username and password. That account can then manage everyone else from
**Administration** in the username menu in the header (only visible to admins) — create additional users,
reset a password, or delete an account. Deleting a user never deletes their notes: the folder is
moved to `notes/deleted/<user-id>/` rather than removed, and the account itself is deleted. There's
no "restore" button yet — put the folder back and re-create the account by hand if you need to.

**Each user needs a password, an email (for OIDC), or both** — set when they're created. There's
no self-service password reset or invite email (JNotes has no mail-sending capability); an admin
sets or resets a password directly, and anyone can change their own from **My Account** (click your name in the header).

**OIDC** is optional and configured with the `OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_CLIENT_SECRET`
env vars (see [Configuration](configuration.md)) — once set, a "Sign in with …" button appears on the login page.
Accounts aren't auto-created from OIDC: an admin creates the user first with their email set, and
that account links to the IdP automatically the first time they sign in with a matching email.
Once OIDC is configured, an admin can turn password login off entirely from
Administration → Security (refused if OIDC isn't configured, so you can't lock everyone out).
