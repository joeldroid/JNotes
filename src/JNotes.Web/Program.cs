using JNotes.Core;
using JNotes.Core.Users;
using JNotes.Web;
using JNotes.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddJNotesCore(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, CookieCurrentUserContext>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

#if DEBUG
// Live-recompiles Styles/*.scss to wwwroot/css/app.css while running under `dotnet watch`.
// Production builds don't need this — the Sass compiler's MSBuild task already compiles the
// current .scss into .css as part of `dotnet build`/`dotnet publish`.
builder.Services.AddSassCompiler();
#endif

var app = builder.Build();

// Apply pending EF Core migrations, ensure at least one user account exists (bootstrapping a
// default "admin" if none do), and reconcile every user's SQLite index against their notes folder
// before serving traffic, so files added/edited/removed while the app wasn't running are picked
// up immediately. See JNotes.Core.HostExtensions.
await app.Services.MigrateJNotesDatabaseAsync();
await app.Services.EnsureDefaultUserAsync();
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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
