using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialAgentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalInstances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    OwnedByServitium = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LaunchJson = table.Column<string>(type: "TEXT", nullable: false),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalJavaRuntimes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    JavaExecutablePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Architecture = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Imported = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalJavaRuntimes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalOutboxEvents",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Critical = table.Column<bool>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalOutboxEvents", x => x.Sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalInstances_Name",
                table: "LocalInstances",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalInstances_WorkingDirectory",
                table: "LocalInstances",
                column: "WorkingDirectory",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalJavaRuntimes_JavaExecutablePath",
                table: "LocalJavaRuntimes",
                column: "JavaExecutablePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalOutboxEvents_OccurredAt",
                table: "LocalOutboxEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalInstances");

            migrationBuilder.DropTable(
                name: "LocalJavaRuntimes");

            migrationBuilder.DropTable(
                name: "LocalOutboxEvents");
        }
    }
}
