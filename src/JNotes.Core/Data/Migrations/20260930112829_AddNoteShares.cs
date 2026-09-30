using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JNotes.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteShares",
                columns: table => new
                {
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedWithUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteShares", x => new { x.NoteId, x.SharedWithUserId });
                    table.CheckConstraint("CK_NoteShares_NotSelf", "\"OwnerUserId\" <> \"SharedWithUserId\"");
                    table.ForeignKey(
                        name: "FK_NoteShares_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NoteShares_Users_SharedWithUserId",
                        column: x => x.SharedWithUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_OwnerUserId_NoteId",
                table: "NoteShares",
                columns: new[] { "OwnerUserId", "NoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_SharedWithUserId",
                table: "NoteShares",
                column: "SharedWithUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteShares");
        }
    }
}
