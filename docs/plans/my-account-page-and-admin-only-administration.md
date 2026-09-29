# Plan: "My Account" page, admin-only "Administration"

Move the Account card out of `/settings` into a new `/account` page ("My Account"), linked from the
username menu in the header. Rename the sidebar "Settings" link to "Administration" and show it only to
admins; `/settings` itself becomes admin-only server-side.

> **Follow-up change (user request after execution):** the "Administration" link was moved out of the
> sidebar (`NavMenu.razor`) into the username menu, next to "My Account" and above Sign out. It is an
> admin-only `MudMenuItem Href="/settings"` on desktop and `MudButton Href="/settings"` (`jn-account-link`) in
> the mobile collapse, each wrapped in `<AuthorizeView Roles="Admin" Context="adminAuth">`. Wherever this plan
> says "sidebar link"/"Administration before Trash", read "account menu item" instead; the sidebar now has
> no Administration entry. Route protection (`[Authorize(Roles = "Admin")]` on `/settings`) is unchanged.

## Goal

Every signed-in user gets `/account`, reached from the username menu in the app bar (and the same widget
at the bottom of the sidebar on mobile). It holds first/last name, email, Save profile and the Change
password link. `/settings` becomes an admin-only "Administration" page enforced by the route, and its
sidebar link is renamed "Administration" and wrapped in `<AuthorizeView Roles="Admin">`.

## Context (verified by the planner)

- `Components/Pages/Settings.razor`: `@page "/settings"`, `[Authorize]`. Lines ~26-56 are the "App settings"
  section with the Account `MudPaper` (`jn-settings-card jn-settings-card-narrow`). Lines ~58-346 are
  `<AuthorizeView Roles="Admin" Context="adminAuthContext">` with the Administration section and the three
  dialogs. Account state lives in `_currentUserAccount`, `_profileFirstName`, `_profileLastName`,
  `_profileEmail`, `_canEditEmail`, `_isSavingProfile`, `_profileError` (~349-356). `OnInitializedAsync`
  loads the profile (~409-416), then admin data behind `IsCurrentUserAdminAsync()` (~418-433).
  `SaveProfileAsync` (~500-535) calls `UpdateProfileAsync(CurrentUser.UserId, CurrentUser.UserId, ...)` then
  `NavigationManager.NavigateTo("/settings", forceLoad: true)` so `MainLayout`'s name refreshes.
  `ConfirmEditProfileAsync` (~663) also force-reloads `/settings` for an admin editing their own row; its
  comment (~662) still says "Profile.razor" (stale).
- `Components/Pages/ChangePassword.razor`: `@page "/settings/change-password"`, `[Authorize]`; Cancel
  (~line 44) `Href="/settings"`, success (~line 101) `NavigateTo("/settings")`.
- `Components/Layout/AccountMenu.razor`: desktop (`InAppBar`) `MudMenu` whose `ChildContent` is only the
  `/logout` form; mobile `MudCollapse` holds only the `/logout` form. Injects nothing.
- `Components/Layout/NavMenu.razor` ~line 34: `<MudNavLink Href="/settings" ...>Settings</MudNavLink>`, not
  wrapped in an `AuthorizeView`.
- `MainLayout.razor` looks up `_displayName`/`_initials` once per circuit, hence the forced reload after a
  self-edit.
- `Routes.razor`: an authenticated non-admin hitting a role-gated page sees "You are not authorized to view
  this page." (no redirect loop).
- MudBlazor 9.10.0 `MudMenuItem` supports `Href`, `Icon`, `AutoClose`, `OnClick`; a navigation-only
  `MudMenuItem Href` is valid. The "never a MudMenuItem" rule applies only to sign-out (needs a form POST
  for `Set-Cookie`).

## Decisions

1. **New route `/account`** in `Components/Pages/Account.razor`, `[Authorize]`, title/h1 "My Account".
   Injects `IUserRepository`, `IUserManagementService`, `ICurrentUserContext`, `NavigationManager`; reads
   `CurrentUser.UserId` in `OnInitializedAsync` (first lifecycle method, satisfies the
   `ICurrentUserContext` gotcha).
2. **Keep `/settings` as the Administration page, now `[Authorize(Roles = nameof(UserRole.Admin))]`.**
   Bookmarks keep working; a Member gets the NotAuthorized message rather than an empty page. Keep the inner
   `<AuthorizeView Roles="Admin">` and the `IsCurrentUserAdminAsync()` guard as defense in depth (an open
   circuit keeps its principal until revalidation). File name stays `Settings.razor`.
3. **Move Change password to `/account/change-password`**; Cancel and success return to `/account`. Still
   `[Authorize]` with no role.
4. **"My Account" in both AccountMenu placements, above Sign out.** Sign-out stays a plain form POST.
   - Desktop: `<MudMenuItem Href="/account" Icon="@Icons.Material.Outlined.AccountCircle">My Account</MudMenuItem>`
     before the `<form>` in `ChildContent`.
   - Mobile collapse: `<MudButton Href="/account" Variant="Variant.Text" StartIcon="@Icons.Material.Outlined.AccountCircle" Size="Size.Small" FullWidth="true" Class="jn-account-link">My Account</MudButton>`
     before the form. `MainLayout` already closes the overlay on navigation below md.
5. **Display-name refresh:** `Account.SaveProfileAsync` does `NavigateTo("/account", forceLoad: true)`.
   The admin own-row edit in `Settings.razor` keeps reloading `/settings`.
6. **CSS:** reuse `jn-settings-card jn-settings-card-narrow`; add one `.jn-account-link` rule in
   `nav-menu.css`; update the header comment of `settings.css`.
7. **Nav:** `<AuthorizeView Roles="Admin"><MudNavLink Href="/settings" Icon="@Icons.Material.Outlined.AdminPanelSettings">Administration</MudNavLink></AuthorizeView>`,
   same position (before Trash). No nav entry for My Account. If the icon name doesn't compile, fall back to
   `Icons.Material.Outlined.Settings`.

## Steps

### Phase 1: new page and change-password route

1. Create `src/JNotes.Web/Components/Pages/Account.razor`:
   - `@page "/account"`, `@attribute [Authorize]`, the four `@inject`s above.
   - `<PageTitle>My Account</PageTitle>`, `<MudText Typo="Typo.h5" HtmlTag="h1" Class="mb-3">My Account</MudText>`.
   - Move the Account `MudPaper` from Settings.razor unchanged; drop its "Account" h3 (duplicates the h1).
     Change password button `Href="/account/change-password"`. No `<section>`/"App settings" wrapper.
   - `@code`: move the seven profile fields, the profile-loading block of `OnInitializedAsync`
     (`GetUserByIdAsync(CurrentUser.UserId)`) and `SaveProfileAsync` verbatim, except navigate to
     `"/account"` with `forceLoad: true` and update the comment (MainLayout is a sibling; the reload
     refreshes the name/initials in the app bar and sidebar).
   - Keep the email lock: disabled input, resend stored email when `!_canEditEmail`.
2. Edit `ChangePassword.razor`: `@page "/account/change-password"`; Cancel `Href="/account"`; success
   `NavigateTo("/account")`.

### Phase 2: Settings becomes Administration

3. Edit `Settings.razor`:
   - `@attribute [Authorize(Roles = nameof(UserRole.Admin))]`.
   - `<PageTitle>Administration</PageTitle>` and h1 "Administration".
   - Delete the "App settings" section; keep `<AuthorizeView Roles="Admin" ...>` and contents; remove the
     now-redundant "Administration settings" h2.
   - `@code`: delete the account fields, the profile block in `OnInitializedAsync`, and `SaveProfileAsync`.
     Keep the `IsCurrentUserAdminAsync()` guard; update its comment (route is role-gated; guard is defense
     in depth).
   - Fix the stale "Profile.razor" comment near `ConfirmEditProfileAsync` to reference Account.razor. Leave
     its `/settings` forceLoad.
   - Check each `@inject` before removing; likely all still used by admin code.

### Phase 3: nav and account menu

4. `NavMenu.razor`: replace the Settings link with the `AuthorizeView`-wrapped Administration link.
5. `AccountMenu.razor`: add the "My Account" `MudMenuItem` (desktop) and `MudButton` (mobile) before the
   sign-out form; update the header comment.
6. `wwwroot/css/nav-menu.css`: add `.jn-account-link { margin: 0.5rem 0.25rem 0; justify-content: flex-start; }`
   next to `.jn-signout`; tune if it looks off.
7. `wwwroot/css/settings.css` line 1: comment only ("Administration (/settings) and My Account (/account)
   pages: ...").

### Phase 4: docs

8. **`CLAUDE.md`** ("Authentication and users" and "Sidebar" sections) updates. Applied after the user
   approved them (not part of the executor's scope):
   - Profile-editing bullet: self-edit now lives in `Account.razor` (`/account`); forced reload targets the
     current page (`/account`, or `/settings` for an admin editing their own row).
   - Password bullet: `/settings/change-password` becomes `/account/change-password`; "My Account's Change
     password button".
   - "Settings and user management share one page" bullet: `/settings` is `[Authorize(Roles = "Admin")]`
     titled "Administration"; personal settings live on `/account`; inner `AuthorizeView` and `IsInRole`
     guard are defense in depth; drop "App settings".
   - Sidebar: Account widget holds a "My Account" link above sign-out; Order bullet "Settings, Trash"
     becomes "Administration (admins only), Trash".
9. **`README.md`**: "Settings → Administration settings" becomes "Administration"; "Settings → Rescan Now"
   becomes "Administration → Rescan Now" (~lines 24, 102, 117, 189); ~227 "Settings → Administration
   has…" becomes "Administration has…"; ~253-254 "from Settings → **Administration settings** (only
   visible once signed in as an admin)" becomes "from **Administration** in the sidebar (only visible to
   admins)"; ~261 "from Settings → Account" becomes "from **My Account** (click your name in the header)";
   ~267-268 "Settings → Administration settings → Security" becomes "Administration → Security". Don't touch
   other `docs/plans/*` (historical).
10. Run `dotnet format JNotes.slnx`.

## Tests

None to add: no test references these routes and there are no Razor/bUnit tests. Core is unchanged
(`UpdateProfileAsync`'s admin-only email check is already covered by `UserManagementServiceTests`). Run the
full suite as a regression check.

## Verification checklist (tester)

Setup: throwaway `POSTGRES_DB` and `NOTES_PATH`; `OLLAMA_URL` empty; use standing `jnotes-debug-db`
(never stop/remove the debug containers).

1. `dotnet build JNotes.slnx`: no errors, no banned-API or unused-inject warnings.
2. `dotnet test JNotes.slnx`: green (needs `jnotes-debug-db` on `localhost:5432`).
3. `dotnet format JNotes.slnx --verify-no-changes`: clean.
4. Run the app, complete `/setup` as admin, create a Member with a password under Administration.
5. **Admin, desktop (>= md):**
   - Sidebar shows "Administration" before Trash, no "Settings".
   - `/settings` shows title/h1 "Administration"; Security, Storage, Index, Images, Auto-tagging and user
     management work (incl. Rescan Now); no Account card.
   - App-bar name menu shows "My Account" and "Sign out"; My Account lands on `/account` and closes the menu.
   - `/account`: email editable; change first name and Save profile: page reloads, header name and initials
     update.
   - Change password: URL `/account/change-password`; Cancel returns to `/account`; success returns to
     `/account`; re-login with the new password works.
   - Administration → Edit profile on own row → Save reloads `/settings`, header name updates.
6. **Member:**
   - No Administration link; sign-out (form POST) still works.
   - `/settings` typed directly shows "You are not authorized to view this page."
   - `/account`: email disabled with "Only an admin can change your email."; changing first name updates the
     header.
   - Change password requires the current password; a wrong one errors.
7. **Mobile (< md), both roles:** drawer account widget shows "My Account" and "Sign out"; My Account goes to
   `/account` and closes the drawer; Administration visible for admin only.
8. `/settings/change-password` now 404s (expected).
9. Browser console: no errors on any of these pages.

## Risks and edge cases

- A user demoted while their circuit is open keeps the old principal until
  `JNotesRevalidatingAuthenticationStateProvider` fires; don't remove the inner `AuthorizeView`/`IsInRole`
  guard as "redundant".
- Sign-out must never become a `MudMenuItem`/`OnClick` (needs `Set-Cookie`).
- `Account.razor` must resolve `CurrentUser` in `OnInitializedAsync`, never lazily behind a click.
- `AdminPanelSettings` icon name is only verified by building.

## Open questions (defaults chosen)

- Keep `/settings/change-password` as an alias? Default: no.
- Rename `/settings` to `/admin`? Default: no; only the label changes.

## Out of scope

Core services, repositories, migrations, `UpdateProfileAsync` authorization; `/login`, `/setup`,
`AuthLayout`, `Program.cs` auth wiring and the `/logout` form mechanics; the Administration cards and
dialogs beyond removing the Account card and renaming headings; other `docs/plans/*`; docker/debug containers.
