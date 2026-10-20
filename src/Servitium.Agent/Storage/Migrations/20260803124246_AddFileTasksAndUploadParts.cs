using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddFileTasksAndUploadParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalConfigBackups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalConfigBackups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalFileTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    BytesCompleted = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalFileTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalUploadParts",
                columns: table => new
                {
                    UploadId = table.Column<string>(type: "TEXT", nullable: false),
                    PartIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalUploadParts", x => new { x.UploadId, x.PartIndex });
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalConfigBackups_InstanceId_RelativePath_CreatedAtUnixMilliseconds",
                table: "LocalConfigBackups",
                columns: new[] { "InstanceId", "RelativePath", "CreatedAtUnixMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalFileTasks_InstanceId_Status",
                table: "LocalFileTasks",
                columns: new[] { "InstanceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalFileTasks_UpdatedAtUnixMilliseconds",
                table: "LocalFileTasks",
                column: "UpdatedAtUnixMilliseconds");

            migrationBuilder.CreateIndex(
                name: "IX_LocalUploadParts_ExpiresAtUnixMilliseconds",
                table: "LocalUploadParts",
                column: "ExpiresAtUnixMilliseconds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalConfigBackups");

            migrationBuilder.DropTable(
                name: "LocalFileTasks");

            migrationBuilder.DropTable(
                name: "LocalUploadParts");
        }
    }
}
