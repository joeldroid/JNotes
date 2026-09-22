using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JNotes.Core.Data;

/// <summary>
/// Lets `dotnet ef` commands (migrations add/update) construct a <see cref="JNotesDbContext"/>
/// standalone, without spinning up the full Web host/DI container. Only used at design time —
/// the app itself gets its connection string from <see cref="JNotesOptions"/> via DI (see
/// <c>ServiceCollectionExtensions.AddJNotesCore</c>).
/// </summary>
public sealed class JNotesDbContextFactory : IDesignTimeDbContextFactory<JNotesDbContext>
{
    public JNotesDbContext CreateDbContext(string[] args)
    {
        // `dotnet ef migrations add` never connects, so any well-formed string works; reading the env
        // var first means `dotnet ef database update` can still reach a real database if you want it
        // to (the app migrates itself at startup, so you normally don't need to).
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=jnotes;Username=jnotes;Password=jnotes";

        var optionsBuilder = new DbContextOptionsBuilder<JNotesDbContext>()
            .UseNpgsql(connectionString);

        return new JNotesDbContext(optionsBuilder.Options);
    }
}
