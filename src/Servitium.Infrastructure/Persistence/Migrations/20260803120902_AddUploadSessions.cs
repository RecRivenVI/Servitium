using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    PartSizeBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    PartCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceivedPartsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Overwrite = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ExpiresAtUnixMilliseconds",
                table: "UploadSessions",
                column: "ExpiresAtUnixMilliseconds");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_InstanceId_NodeId",
                table: "UploadSessions",
                columns: new[] { "InstanceId", "NodeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UploadSessions");
        }
    }
}
