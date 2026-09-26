using JNotes.Core;
using JNotes.Core.Data;
using JNotes.Core.Export;
using JNotes.Core.Images;
using JNotes.Core.Tagging;
using JNotes.Core.Users;
using JNotes.Web;
using JNotes.Web.Auth;
using JNotes.Web.Components;
using JNotes.Web.Components.Layout;
using JNotes.Web.Export;
using JNotes.Web.Images;
using JNotes.Web.Indexing;
using JNotes.Web.Theme;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddJNotesCore(builder.Configuration);
// Periodic index reconciliation — interval controlled by AUTO_INDEX_INTERVAL_MINUTES (0 disables).
builder.Services.AddHostedService<AutoIndexBackgroundService>();
// Periodic unused-image cleanup — unlike the above, the interval is an admin setting in the app (default 7 days, 0 = off).
builder.Services.AddHostedService<ImageCleanupBackgroundService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, AuthenticatedCurrentUserContext>();
// Same Core-interface, Web-implementation split as ICurrentUserContext: the PDF's static assets are Web's
// wwwroot. Registered always (even with the feature off) so Development's ValidateOnBuild can resolve NoteExportService.
builder.Services.AddSingleton<IPdfExportAssetSource, WebRootPdfExportAssetSource>();

// Persisted alongside the notes (the same bind-mounted volume in Docker) rather than the
// container's ephemeral filesystem — without this, the keys used to sign the auth cookie are
// regenerated on every restart, silently signing everyone out (ASP.NET Core logs a DataProtection
// warning about exactly this if left at its default).
// This used to derive from the old SQLite database file's directory; there is no local database
// file any more (Postgres may be on another host entirely), so it's its own key. When unset it
// falls back to a "keys" folder beside NOTES_PATH, which reproduces the old location for both defaults
// (App_Data/notes -> App_Data/keys, /data/notes -> /data/keys). Read directly off IConfiguration,
// like the OIDC values below, since this must be decided before the DI container exists.
var notesPath = builder.Configuration["NOTES_PATH"] ?? "notes";
var keysDirectory = builder.Configuration["DATA_PROTECTION_KEYS_PATH"]
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(notesPath))!, "keys");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(UserRole.Admin)));
});
builder.Services.AddCascadingAuthenticationState();
// Registered after AddCascadingAuthenticationState() so it wins (last registration for a service wins).
builder.Services.AddScoped<AuthenticationStateProvider, JNotesRevalidatingAuthenticationStateProvider>();

var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = CookiePrincipalValidator.ValidateAsync;
    });

// OidcOptions is normally resolved through DI once the container is built, but AddOpenIdConnect
// must be decided here, at registration time — read the same flat env vars AddJNotesCore reads,
// directly off IConfiguration, just for this one startup-time decision.
var oidcAuthority = builder.Configuration["OIDC_AUTHORITY"];
var oidcClientId = builder.Configuration["OIDC_CLIENT_ID"];
var oidcClientSecret = builder.Configuration["OIDC_CLIENT_SECRET"];
var oidcConfigured = !string.IsNullOrWhiteSpace(oidcAuthority) && !string.IsNullOrWhiteSpace(oidcClientId) && !string.IsNullOrWhiteSpace(oidcClientSecret);

if (oidcConfigured)
{
    authBuilder.AddOpenIdConnect(options =>
    {
        options.Authority = oidcAuthority;
        options.ClientId = oidcClientId;
        options.ClientSecret = oidcClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        // Raw IdP claim names ("sub"/"email") come through unmapped — avoids legacy long-URI remapping surprises.
        options.MapInboundClaims = false;
        options.Events = new OpenIdConnectEvents { OnTokenValidated = OidcAccountLinker.OnTokenValidatedAsync };
    });
}

builder.Services.AddMudServices();
builder.Services.AddScoped<ThemeState>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Apply pending EF Core migrations, seed the in-memory "has anyone been set up yet" flag, and
// reconcile every user's PostgreSQL note index against their notes folder before serving traffic, so
// files added/edited/removed while the app wasn't running are picked up immediately. See
// JNotes.Core.HostExtensions. There's no more auto-created default user — the first admin is
// created for real, with a real password, through /setup.
await app.Services.MigrateJNotesDatabaseAsync();
await app.Services.InitializeSetupStateAsync();
await app.Services.RunStartupReconciliationAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Redirects every request to /setup until the first admin exists — no way to use the app (or even
// see /login) before that. Once true, /setup itself redirects away (see Setup.razor). Requests for
// a static asset (css/js/fonts/images — anything with a file extension) and the SignalR circuit
// endpoint are let through untouched, or /setup's own page would render unstyled/non-interactive.
app.Use(async (context, next) =>
{
    var setupState = context.RequestServices.GetRequiredService<ISetupStateProvider>();
    var path = context.Request.Path;
    var isAsset = Path.HasExtension(path.Value) || path.StartsWithSegments("/_blazor");

    if (!setupState.IsSetupComplete && !path.StartsWithSegments("/setup") && !isAsset)
    {
        context.Response.Redirect("/setup");
        return;
    }

    await next(context);
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/login/oidc", (string? returnUrl) =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl },
        [OpenIdConnectDefaults.AuthenticationScheme]))
    .AllowAnonymous();

// Serves an uploaded image from the *caller's own* images folder, so another user's file name is
// just a 404 — no user id in the URL to spoof. Names are generated GUIDs and never reused, hence
// the long immutable cache; nosniff so a browser never second-guesses the image content type.
app.MapGet("/images/{fileName}", async (string fileName, IImageService images, HttpContext context, CancellationToken cancellationToken) =>
{
    var image = await images.OpenAsync(fileName, cancellationToken);
    if (image is null)
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.Stream(image.Stream, image.ContentType);
}).RequireAuthorization();

// Optional PDF export: mapped only when GOTENBERG_URL is valid, so with the feature off the route simply 404s like any unknown URL.
var pdfExport = app.Services.GetRequiredService<IOptions<PdfExportOptions>>().Value;
if (pdfExport.IsEnabled)
{
    app.MapNotePdfExport();
}
else if (!string.IsNullOrWhiteSpace(pdfExport.GotenbergUrl))
{
    // Never log the URL itself; it may contain credentials.
    app.Logger.LogWarning("GOTENBERG_URL is set but isn't an absolute http(s) URL — PDF export is disabled.");
}

// Optional auto-tagging: nothing to map (the editor calls INoteAutoTagService over the circuit), so just say what state it's in.
var autoTag = app.Services.GetRequiredService<IOptions<AutoTagOptions>>().Value;
if (autoTag.IsEnabled)
{
    app.Logger.LogInformation("Auto-tagging is enabled (Ollama model {Model}).", autoTag.Model);
}
else if (autoTag.Enabled)
{
    // Never log the URL itself.
    app.Logger.LogWarning("AUTO_TAG_ENABLED is set but OLLAMA_URL isn't an absolute http(s) URL — auto-tagging is disabled.");
}

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.Run();
