using JNotes.Core.Data;
using JNotes.Core.Indexing;
using JNotes.Core.Notes;
using JNotes.Core.Serialization;
using JNotes.Core.Settings;
using JNotes.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JNotes.Core;

/// <summary>
/// The one place Core's services are wired up. <c>JNotes.Web</c>'s <c>Program.cs</c> calls only
/// this (plus <see cref="HostExtensions"/>) — it never registers a <see cref="DbContext"/> or
/// file-store implementation itself, which is what keeps Web from having a reason to reach past
/// <c>INoteService</c>/<c>ISettingsService</c>/<c>INoteIndexReconciler</c> into Core's internals.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJNotesCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JNotesOptions>(configuration.GetSection(JNotesOptions.SectionName));

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
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<INoteFileStore, FileSystemNoteFileStore>();
        services.AddScoped<INoteIndexRepository, EfNoteIndexRepository>();
        services.AddScoped<INoteService, NoteService>();
        services.AddScoped<INoteIndexReconciler, NoteIndexReconciler>();

        return services;
    }
}
