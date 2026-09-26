using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JNotes.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteIndexUserTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteIndexUserTags",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tag = table.Column<string>(type: "text", nullable: false),
                    NoteCount = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteIndexUserTags", x => new { x.UserId, x.Tag });
                });

            // Backfill so suggestions work right after upgrade, before the first rescan rebuilds the table.
            migrationBuilder.Sql("""
                INSERT INTO "NoteIndexUserTags" ("UserId","Tag","NoteCount","LastUsedAt")
                SELECT e."UserId", t."Tag", COUNT(DISTINCT e."Id"), MAX(e."UpdatedAt")
                FROM "NoteIndexTags" t JOIN "NoteIndexEntries" e ON e."Id" = t."NoteIndexEntryId"
                GROUP BY e."UserId", t."Tag";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteIndexUserTags");
        }
    }
}
