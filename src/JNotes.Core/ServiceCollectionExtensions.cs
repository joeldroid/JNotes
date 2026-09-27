using JNotes.Core.Data;
using JNotes.Core.Export;
using JNotes.Core.Images;
using JNotes.Core.Indexing;
using JNotes.Core.Notes;
using JNotes.Core.Serialization;
using JNotes.Core.Settings;
using JNotes.Core.Storage;
using JNotes.Core.Tagging;
using JNotes.Core.Time;
using JNotes.Core.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace JNotes.Core;

/// <summary>
/// The one place Core's services are wired up. <c>JNotes.Web</c>'s <c>Program.cs</c> calls only
/// this (plus <see cref="HostExtensions"/>) — it never registers a <see cref="DbContext"/> or
/// file-store implementation itself, which is what keeps Web from having a reason to reach past
/// <c>INoteService</c>/<c>ISettingsService</c>/<c>INoteIndexReconciler</c>/<c>IUserRepository</c>/
/// <c>IUserManagementService</c>/<c>IImageService</c>/<c>INoteExportService</c>/<c>INoteAutoTagger</c> into Core's internals.
/// Two deliberate exceptions: <c>ICurrentUserContext</c> and <c>IPdfExportAssetSource</c> are declared
/// in Core but registered by <c>JNotes.Web</c> itself, not here — the former's only implementation
/// needs <c>IHttpContextAccessor</c>, an ASP.NET Core hosting type Core has never depended on; the
/// latter's files (mermaid, Roboto) are Web's <c>wwwroot</c>. Web's own auth wiring (cookie/OIDC
/// handlers, sign-in/out) lives there for the same reason.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJNotesCore(this IServiceCollection services, IConfiguration configuration)
    {
        // Flat "NOTES_PATH"/"POSTGRES_HOST"/"TZ" keys rather than a nested section — these are the
        // exact names the app expects as environment variables (see Dockerfile/docker-compose.yml),
        // and IConfiguration's indexer already reads plain env vars by that name with no "__" nesting
        // needed, so appsettings.json declares them the same way for local dev.
        services.Configure<JNotesOptions>(options =>
        {
            options.NotesFolderPath = configuration["NOTES_PATH"] ?? options.NotesFolderPath;
            options.TimeZoneId = configuration["TZ"] ?? options.TimeZoneId;
            options.PostgresHost = configuration["POSTGRES_HOST"] ?? options.PostgresHost;
            options.PostgresPort = int.TryParse(configuration["POSTGRES_PORT"], out var port) ? port : options.PostgresPort;
            options.PostgresDatabase = configuration["POSTGRES_DB"] ?? options.PostgresDatabase;
            options.PostgresUsername = configuration["POSTGRES_USER"] ?? options.PostgresUsername;
            options.PostgresPassword = configuration["POSTGRES_PASSWORD"] ?? options.PostgresPassword;
            // POSTGRES_HOST/PORT/DB/USER/PASSWORD split out (rather than one POSTGRES_CONNECTION_STRING)
            // so a docker-compose setup can share the same plain values with the `postgres` service
            // instead of hand-building an Npgsql connection string. Built here, not read directly, so
            // it's still one string by the time DbContextFactory needs it; CoreServicesFixture and the
            // other test fixtures set PostgresConnectionString directly afterwards, which overrides this.
            if (!string.IsNullOrWhiteSpace(options.PostgresHost))
            {
                options.PostgresConnectionString = new NpgsqlConnectionStringBuilder
                {
                    Host = options.PostgresHost,
                    Port = options.PostgresPort ?? 5432,
                    Database = options.PostgresDatabase,
                    Username = options.PostgresUsername,
                    Password = options.PostgresPassword,
                }.ConnectionString;
            }
            // Parsed (not just "?? default") because the raw string needs validating/clamping — see AutoIndexInterval.
            options.AutoIndexIntervalMinutes = AutoIndexInterval.ParseMinutes(configuration["AUTO_INDEX_INTERVAL_MINUTES"]);
        });

        // Same flat-env-var pattern for OIDC — optional, unset means "not configured" (see
        // OidcOptions.IsConfigured). ClientSecret is a secret, but this app has never had a
        // separate secrets mechanism (POSTGRES_PASSWORD etc. work the same way) — never logged.
        services.Configure<OidcOptions>(options =>
        {
            options.Authority = configuration["OIDC_AUTHORITY"] ?? options.Authority;
            options.ClientId = configuration["OIDC_CLIENT_ID"] ?? options.ClientId;
            options.ClientSecret = configuration["OIDC_CLIENT_SECRET"] ?? options.ClientSecret;
            options.DisplayName = configuration["OIDC_DISPLAY_NAME"] ?? options.DisplayName;
        });

        // Optional PDF export — same flat-env-var pattern; unset/invalid GOTENBERG_URL means off (PdfExportOptions.IsEnabled).
        services.Configure<PdfExportOptions>(options => options.GotenbergUrl = configuration["GOTENBERG_URL"] ?? options.GotenbergUrl);
        services.AddHttpClient<IPdfRenderer, GotenbergPdfRenderer>((sp, client) =>
        {
            if (sp.GetRequiredService<IOptions<PdfExportOptions>>().Value.GotenbergBaseUri is { } baseUri)
            {
                client.BaseAddress = baseUri;
            }

            client.Timeout = GotenbergPdfRenderer.Timeout;
        });
        services.AddScoped<INoteExportService, NoteExportService>();
        // IPdfExportAssetSource is intentionally NOT registered here — like ICurrentUserContext, JNotes.Web does (its files are Web's wwwroot).

        // Optional background Ollama auto-tagging — off unless OLLAMA_URL is a valid http(s) URL (AutoTagOptions.IsEnabled).
        services.Configure<AutoTagOptions>(options =>
        {
            options.OllamaUrl = configuration["OLLAMA_URL"] ?? options.OllamaUrl;
            if (!string.IsNullOrWhiteSpace(configuration["OLLAMA_MODEL"]))
            {
                options.Model = configuration["OLLAMA_MODEL"]!.Trim();
            }

            options.Timeout = AutoTagOptions.ParseTimeout(configuration["OLLAMA_TIMEOUT_SECONDS"]);
            options.IntervalMinutes = AutoTagOptions.ParseIntervalMinutes(configuration["AUTO_TAG_INTERVAL_MINUTES"]);
            options.BatchSize = AutoTagOptions.ParseBatchSize(configuration["AUTO_TAG_BATCH_SIZE"]);
        });
        services.AddHttpClient<ITagModelClient, OllamaTagModelClient>((sp, client) =>
        {
            var autoTag = sp.GetRequiredService<IOptions<AutoTagOptions>>().Value;
            if (autoTag.OllamaBaseUri is { } baseUri)
            {
                client.BaseAddress = baseUri;
            }

            client.Timeout = autoTag.Timeout;
        })
        // The default handler logs the full request URL at Information on every call; the Ollama URL must never be logged.
        .RemoveAllLoggers();
        services.TryAddSingleton(TimeProvider.System);
        // Not derived from the files, unlike the NoteIndex* tables — see NoteAutoTagAttempt.
        services.AddSingleton<IAutoTagStateRepository, PostgresAutoTagStateRepository>();
        // Scoped like IImageCleanupService: the singleton hosted service that drives it makes a scope per pass.
        services.AddScoped<INoteAutoTagger, NoteAutoTagger>();

        services.AddDbContextFactory<JNotesDbContext>((sp, options) =>
        {
            var jnotesOptions = sp.GetRequiredService<IOptions<JNotesOptions>>().Value;

            // Thrown from here (rather than validated at registration) so it fires the first time a
            // context is created — which is HostExtensions.MigrateJNotesDatabaseAsync, before app.Run().
            // Same "fail loudly at startup, never limp along" property the old eager Redis connect had.
            if (string.IsNullOrWhiteSpace(jnotesOptions.PostgresConnectionString))
            {
                throw new InvalidOperationException(
                    "POSTGRES_HOST (with POSTGRES_DB/POSTGRES_USER/POSTGRES_PASSWORD) is required — JNotes keeps user accounts, settings and the note index in PostgreSQL.");
            }

            options.UseNpgsql(jnotesOptions.PostgresConnectionString);
        });

        services.AddSingleton<INoteMarkdownSerializer, NoteMarkdownSerializer>();
        services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        services.AddSingleton<IAppTimeZoneProvider, AppTimeZoneProvider>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<INoteFileStore, FileSystemNoteFileStore>();
        services.AddScoped<IImageStore, FileSystemImageStore>();
        services.AddScoped<IImageService, ImageService>();
        // Scoped like IAllUsersIndexReconciler: the singleton background service that drives it makes a scope per check.
        services.AddScoped<IImageCleanupService, ImageCleanupService>();
        // Singleton like the Redis repository it replaces — it creates a JNotesDbContext per call
        // through the singleton IDbContextFactory, exactly as SettingsService/EfUserRepository do.
        services.AddSingleton<INoteIndexRepository, PostgresNoteIndexRepository>();
        services.AddScoped<INoteService, NoteService>();
        services.AddScoped<INoteIndexReconciler, NoteIndexReconciler>();
        // Scoped, matching INoteIndexReconciler/IUserRepository — the singleton background service
        // that drives this must never capture it directly; it creates a scope per tick instead.
        services.AddScoped<IAllUsersIndexReconciler, AllUsersIndexReconciler>();
        services.AddSingleton<PasswordHasher<UserAccount>>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddSingleton<SetupStateProvider>();
        services.AddSingleton<ISetupStateProvider>(sp => sp.GetRequiredService<SetupStateProvider>());
        // ICurrentUserContext is intentionally NOT registered here — see the class doc comment.

        return services;
    }
}
