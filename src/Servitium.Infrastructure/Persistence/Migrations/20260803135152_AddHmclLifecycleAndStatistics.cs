using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHmclLifecycleAndStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IntegrityCheckedAt",
                table: "HmclChannels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrityStatus",
                table: "HmclChannels",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<string>(
                name: "LinkedInstanceId",
                table: "HmclChannels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HmclAccessSources",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    PrefixHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmclAccessSources", x => new { x.ChannelId, x.ReleaseId, x.Date, x.PrefixHash });
                });

            migrationBuilder.CreateTable(
                name: "HmclAccessStats",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    Manifest200 = table.Column<long>(type: "INTEGER", nullable: false),
                    Manifest304 = table.Column<long>(type: "INTEGER", nullable: false),
                    File200 = table.Column<long>(type: "INTEGER", nullable: false),
                    File206 = table.Column<long>(type: "INTEGER", nullable: false),
                    Errors = table.Column<long>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmclAccessStats", x => new { x.ChannelId, x.ReleaseId, x.Date });
                });

            migrationBuilder.CreateIndex(
                name: "IX_HmclAccessSources_Date",
                table: "HmclAccessSources",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_HmclAccessStats_Date",
                table: "HmclAccessStats",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HmclAccessSources");

            migrationBuilder.DropTable(
                name: "HmclAccessStats");

            migrationBuilder.DropColumn(
                name: "IntegrityCheckedAt",
                table: "HmclChannels");

            migrationBuilder.DropColumn(
                name: "IntegrityStatus",
                table: "HmclChannels");

            migrationBuilder.DropColumn(
                name: "LinkedInstanceId",
                table: "HmclChannels");
        }
    }
}
