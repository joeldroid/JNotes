using JNotes.Core.Indexing;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Data;

public sealed class JNotesDbContext(DbContextOptions<JNotesDbContext> options) : DbContext(options)
{
    private const string CaseInsensitiveCollation = "jnotes_case_insensitive";

    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<UserSetting> UserSettings => Set<UserSetting>();

    /// <summary>
    /// The note index — a derived, rebuildable cache over the markdown files, not source data. Safe to
    /// TRUNCATE: NoteIndexReconciler rebuilds it from disk on the next pass (startup, the auto-index
    /// timer, or Settings -> "Rescan Now"). Named NoteIndex* so that's obvious in psql.
    /// </summary>
    public DbSet<NoteIndexEntry> NoteIndex => Set<NoteIndexEntry>();

    public DbSet<NoteIndexTag> NoteIndexTags => Set<NoteIndexTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // PostgreSQL's equivalent of SQLite's NOCASE: a non-deterministic ICU collation makes both the
        // unique index and every "Username == x" comparison case-insensitive, so EfUserRepository needs no
        // ToLower() anywhere. NOTE: this four-argument HasCollation is Npgsql's
        // (NpgsqlModelBuilderExtensions); EF Core's relational HasCollation takes one argument and sets the
        // *database* default collation instead. Both live in the Microsoft.EntityFrameworkCore namespace,
        // so the wrong overload binds silently.
        modelBuilder.HasCollation(CaseInsensitiveCollation, locale: "und-u-ks-level2", provider: "icu", deterministic: false);

        modelBuilder.Entity<SettingEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            // A non-deterministic ICU collation is PostgreSQL's equivalent of SQLite's NOCASE — both
            // the unique index and every "Username == x" comparison stay case-insensitive.
            entity.Property(e => e.Username).UseCollation(CaseInsensitiveCollation);
            entity.HasIndex(e => e.Username).IsUnique();
            // Stored as the enum's name, never its int ordinal — reordering UserRole must never
            // silently reassign who's an admin.
            entity.Property(e => e.Role).HasConversion<string>();
            // Filtered: Email/OidcSubject are optional (password-only accounts have neither). Postgres's
            // plain unique index already tolerates multiple NULLs, so this filter is now belt-and-braces
            // plus a smaller index, not a requirement (unlike under SQLite/EF Core's default behaviour).
            entity.HasIndex(e => e.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");
            entity.HasIndex(e => e.OidcSubject).IsUnique().HasFilter("\"OidcSubject\" IS NOT NULL");
        });

        modelBuilder.Entity<UserSetting>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.Key });
            entity.HasOne<UserAccount>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NoteIndexEntry>(entity =>
        {
            entity.ToTable("NoteIndexEntries");
            entity.HasKey(e => e.Id);
            // Exactly the default note-list query's shape (one user's notes, newest first).
            entity.HasIndex(e => new { e.UserId, e.UpdatedAt }).IsDescending(false, true);
            // Deliberately no FK to Users: the index is a derived cache and must never be able to fail to
            // write because of account-row ordering. (Same as Redis — a deleted user's rows are left
            // behind either way.)
        });

        modelBuilder.Entity<NoteIndexTag>(entity =>
        {
            entity.HasKey(e => new { e.NoteIndexEntryId, e.Ordinal });
            entity.HasIndex(e => e.Tag);
            // FK with no navigation on either side, same as UserSetting -> UserAccount above:
            // NoteIndexEntry must stay the plain DTO INoteIndexRepository/NoteService/NoteIndexReconciler
            // already pass around, and the repository always writes an entry and its tags together.
            entity.HasOne<NoteIndexEntry>()
                .WithMany()
                .HasForeignKey(e => e.NoteIndexEntryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
