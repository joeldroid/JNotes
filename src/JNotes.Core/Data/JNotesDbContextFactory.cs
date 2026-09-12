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
        var optionsBuilder = new DbContextOptionsBuilder<JNotesDbContext>()
            .UseSqlite("Data Source=jnotes.design.db");

        return new JNotesDbContext(optionsBuilder.Options);
    }
}
