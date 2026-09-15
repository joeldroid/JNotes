using JNotes.Core;
using JNotes.Core.Data;
using JNotes.Core.Users;
using JNotes.Web;
using JNotes.Web.Auth;
using JNotes.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddJNotesCore(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, AuthenticatedCurrentUserContext>();

// Persisted alongside the database (same bind-mounted volume in Docker) rather than the container's
// ephemeral filesystem — without this, the keys used to sign the auth cookie are regenerated on
// every restart, silently signing everyone out (ASP.NET Core logs a DataProtection warning about
// exactly this if left at its default). Read directly off IConfiguration, same as JNotesOptions's
// own DB_PATH, since this must be decided before the DI container exists to resolve options from.
var dbPath = builder.Configuration["DB_PATH"] ?? "jnotes.db";
var keysDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "keys");
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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

#if DEBUG
// Live-recompiles Styles/*.scss to wwwroot/css/app.css while running under `dotnet watch`.
// Production builds don't need this — the Sass compiler's MSBuild task already compiles the
// current .scss into .css as part of `dotnet build`/`dotnet publish`.
builder.Services.AddSassCompiler();
#endif

var app = builder.Build();

// Apply pending EF Core migrations, seed the in-memory "has anyone been set up yet" flag, and
// reconcile every user's SQLite index against their notes folder before serving traffic, so files
// added/edited/removed while the app wasn't running are picked up immediately. See
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

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.Run();
