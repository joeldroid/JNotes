using Microsoft.EntityFrameworkCore;

namespace JNotes.Core.Data;

public sealed class JNotesDbContext(DbContextOptions<JNotesDbContext> options) : DbContext(options)
{
    public DbSet<NoteIndexEntry> Notes => Set<NoteIndexEntry>();

    public DbSet<NoteTag> NoteTags => Set<NoteTag>();

    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoteIndexEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UpdatedAt);
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
    }
}
