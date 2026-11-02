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

## Setting up OIDC with Authentik

[Authentik](https://goauthentik.io) works with JNotes' OIDC support out of the box. JNotes uses the
authorization-code flow with PKCE and requests the `openid`, `email` and `profile` scopes, which Authentik
provides by default. Menu names below follow current Authentik releases and may differ slightly in older ones.

### 1. Create the provider and application in Authentik

1. In the Authentik admin interface go to **Applications → Applications → Create with Provider**.
2. **Application:** give it a name (e.g. `JNotes`); note the **slug** it generates (e.g. `jnotes`), because it
   becomes part of the issuer URL.
3. **Provider type:** choose **OAuth2/OpenID Provider**, then set:
   - **Client type:** `Confidential`
   - **Redirect URIs/Origins:** add one entry with matching mode `Strict`:
     `https://notes.example.com/signin-oidc` (your public JNotes URL plus `/signin-oidc`). It must match
     exactly, including `http`/`https` and any port.
   - **Signing Key:** select a key (e.g. `authentik Self-signed Certificate`).
   - **Scopes:** leave the defaults (`openid`, `email`, `profile`).
   - **Subject mode:** leave the default (*Based on the User's hashed ID*). The subject is stored on the JNotes
     account at first sign-in, so don't change it afterwards or linked users won't be recognised.
4. Finish the wizard, then open the new provider and copy its **Client ID** and **Client Secret**.
5. Restrict who may sign in through the application's **Policy / Group / User Bindings** tab if you don't want
   every Authentik user to be able to authenticate to JNotes (they still need a JNotes account either way).

**Logout:** there is nothing to configure. JNotes' **Sign out** only ends the JNotes session (it clears
JNotes' own cookie and returns to `/login`); it does not call Authentik's end-session endpoint, so the user stays
signed in to Authentik and the next "Sign in with …" click signs them straight back in. If your Authentik
version asks for a post-logout redirect URI, you can leave it empty.

### 2. Configure JNotes

Set the three required variables (see [Configuration](configuration.md)):

```env
OIDC_AUTHORITY=https://authentik.example.com/application/o/jnotes/
OIDC_CLIENT_ID=<client id from the provider>
OIDC_CLIENT_SECRET=<client secret from the provider>
OIDC_DISPLAY_NAME=Authentik
```

- `OIDC_AUTHORITY` is the provider's **OpenID Configuration Issuer** (shown on the provider page) with the
  trailing slash: `https://<authentik-host>/application/o/<application-slug>/`. Authentik validates the issuer
  strictly, so a missing slash or the wrong slug fails with a discovery or issuer-mismatch error.
- `OIDC_DISPLAY_NAME` is the label on the "Sign in with …" button.
- With Docker Compose, uncomment the `OIDC_*` lines in `docker-compose.yml` and put the values in your `.env`
  file, then recreate the container (`docker compose up -d`). See [Running with Docker](docker.md).
- **Behind a reverse proxy that terminates TLS** (the usual setup), also set
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (there's a commented line for it next to the `OIDC_*` ones in
  `docker-compose.yml`). Without it JNotes only sees plain HTTP from the proxy, sends
  `redirect_uri=http://…/signin-oidc`, and Authentik rejects the login with a redirect URI error. The proxy must
  forward `X-Forwarded-Proto` and the original `Host` header (most do by default; a hand-written nginx config needs
  `proxy_set_header X-Forwarded-Proto $scheme;` and `proxy_set_header Host $host;`).

### 3. Create the matching JNotes users

JNotes never creates accounts from OIDC. For each person, an admin adds the user under
**Administration** with an **email that exactly matches the email on their Authentik user**. The first
sign-in through Authentik links the two. After that the link uses Authentik's stable subject id, so a later
email change in Authentik doesn't break sign-in.

An Authentik user with no email falls back to their `preferred_username` claim for matching, but setting an
email on the Authentik user is the reliable route.

### Troubleshooting

- **"Redirect URI mismatch" from Authentik:** the redirect URI in the provider must equal
  `https://<your JNotes URL>/signin-oidc` exactly. Look at the `redirect_uri=` parameter in the failing
  `/application/o/authorize/` URL: if it starts with `http%3A`, JNotes doesn't know it's behind HTTPS, so set
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` as described in step 2 and recreate the container.
- **Back on the login page with a "no account" message:** there is no unlinked JNotes account whose email matches
  the Authentik user's, or the account is already linked to a different subject.
- **Startup or sign-in fails fetching the discovery document:** the JNotes container must be able to reach
  `OIDC_AUTHORITY` over the network (DNS and TLS trust included, so a self-signed Authentik certificate needs to
  be trusted by the container).
- **No "Sign in with …" button:** all three of `OIDC_AUTHORITY`, `OIDC_CLIENT_ID` and `OIDC_CLIENT_SECRET` must be
  set and non-blank.
