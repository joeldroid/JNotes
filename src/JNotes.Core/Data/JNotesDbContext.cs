using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Data;

public sealed class JNotesDbContext(DbContextOptions<JNotesDbContext> options) : DbContext(options)
{
    public DbSet<NoteIndexEntry> Notes => Set<NoteIndexEntry>();

    public DbSet<NoteTag> NoteTags => Set<NoteTag>();

    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<UserSetting> UserSettings => Set<UserSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoteIndexEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Searches always filter by user first now, so the useful index is the composite, not
            // UpdatedAt alone.
            entity.HasIndex(e => new { e.UserId, e.UpdatedAt });
            entity.HasOne<UserAccount>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NoteTag>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Tag);
            entity.HasIndex(e => e.NoteId);
            entity.HasOne<NoteIndexEntry>()
                .WithMany()
                .HasForeignKey(e => e.NoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SettingEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            // NOCASE is SQLite's built-in ASCII-only case-insensitive collation — fine for expected
            // usernames, not full Unicode case-folding.
            entity.Property(e => e.Username).UseCollation("NOCASE");
            entity.HasIndex(e => e.Username).IsUnique();
            // Stored as the enum's name, never its int ordinal — reordering UserRole must never
            // silently reassign who's an admin.
            entity.Property(e => e.Role).HasConversion<string>();
            // Filtered: Email/OidcSubject are optional (password-only accounts have neither), and
            // SQLite/EF Core's default unique index would otherwise only tolerate one NULL row.
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
    }
}
