using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Notes;
using JNotes.Core.Serialization;
using JNotes.Core.Settings;
using JNotes.Core.Storage;
using JNotes.Core.Time;
using JNotes.Core.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JNotes.Core;

/// <summary>
/// The one place Core's services are wired up. <c>JNotes.Web</c>'s <c>Program.cs</c> calls only
/// this (plus <see cref="HostExtensions"/>) — it never registers a <see cref="DbContext"/> or
/// file-store implementation itself, which is what keeps Web from having a reason to reach past
/// <c>INoteService</c>/<c>ISettingsService</c>/<c>INoteIndexReconciler</c>/<c>IUserRepository</c>/
/// <c>IUserManagementService</c> into Core's internals. One deliberate exception:
/// <c>ICurrentUserContext</c> is declared in Core but registered by <c>JNotes.Web</c> itself, not
/// here — its only implementation needs <c>IHttpContextAccessor</c>, an ASP.NET Core hosting type
/// Core has never depended on. Web's own auth wiring (cookie/OIDC handlers, sign-in/out) lives
/// there for the same reason.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJNotesCore(this IServiceCollection services, IConfiguration configuration)
    {
        // Flat "NOTES_PATH"/"DB_PATH"/"TZ" keys rather than a nested section — these are the exact
        // names the app expects as environment variables (see Dockerfile/docker-compose.yml), and
        // IConfiguration's indexer already reads plain env vars by that name with no "__" nesting
        // needed, so appsettings.json declares them the same way for local dev.
        services.Configure<JNotesOptions>(options =>
        {
            options.NotesFolderPath = configuration["NOTES_PATH"] ?? options.NotesFolderPath;
            options.DbPath = configuration["DB_PATH"] ?? options.DbPath;
            options.TimeZoneId = configuration["TZ"] ?? options.TimeZoneId;
        });

        // Same flat-env-var pattern for OIDC — optional, unset means "not configured" (see
        // OidcOptions.IsConfigured). ClientSecret is a secret, but this app has never had a
        // separate secrets mechanism (DB_PATH etc. work the same way) — never logged.
        services.Configure<OidcOptions>(options =>
        {
            options.Authority = configuration["OIDC_AUTHORITY"] ?? options.Authority;
            options.ClientId = configuration["OIDC_CLIENT_ID"] ?? options.ClientId;
            options.ClientSecret = configuration["OIDC_CLIENT_SECRET"] ?? options.ClientSecret;
            options.DisplayName = configuration["OIDC_DISPLAY_NAME"] ?? options.DisplayName;
        });

        services.AddDbContextFactory<JNotesDbContext>((sp, options) =>
        {
            var jnotesOptions = sp.GetRequiredService<IOptions<JNotesOptions>>().Value;

            // SQLite won't create a missing parent directory for the db file on its own — ensure
            // it exists before anything tries to open a connection.
            var dbDirectory = Path.GetDirectoryName(jnotesOptions.DbPath);
            if (!string.IsNullOrEmpty(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory);
            }

            options.UseSqlite($"Data Source={jnotesOptions.DbPath}");
        });

        services.AddSingleton<INoteMarkdownSerializer, NoteMarkdownSerializer>();
        services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        services.AddSingleton<IAppTimeZoneProvider, AppTimeZoneProvider>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<INoteFileStore, FileSystemNoteFileStore>();
        services.AddScoped<INoteIndexRepository, EfNoteIndexRepository>();
        services.AddScoped<INoteService, NoteService>();
        services.AddScoped<INoteIndexReconciler, NoteIndexReconciler>();
        services.AddSingleton<PasswordHasher<UserAccount>>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddSingleton<SetupStateProvider>();
        services.AddSingleton<ISetupStateProvider>(sp => sp.GetRequiredService<SetupStateProvider>());
        // ICurrentUserContext is intentionally NOT registered here — see the class doc comment.

        return services;
    }
}
