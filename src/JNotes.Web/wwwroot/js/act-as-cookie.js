// Sets the "act as" dev-only cookie client-side — no server round trip needed, since this isn't a
// security boundary, just enough to click through the app as different users while real login
// doesn't exist yet. See CookieCurrentUserContext.cs. A plain, unsigned, non-HttpOnly cookie is
// fine here precisely because it's not protecting anything.
export function setActAsUser(userId) {
    document.cookie = `actAsUserId=${userId}; path=/; max-age=31536000; samesite=lax`;
}
