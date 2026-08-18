using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHmclReleaseActivations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HmclReleaseActivations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmclReleaseActivations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HmclReleaseActivations_HmclChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "HmclChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HmclReleaseActivations_HmclReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "HmclReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HmclReleaseActivations_ChannelId",
                table: "HmclReleaseActivations",
                column: "ChannelId",
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HmclReleaseActivations_ChannelId_StartedAt",
                table: "HmclReleaseActivations",
                columns: new[] { "ChannelId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HmclReleaseActivations_ReleaseId",
                table: "HmclReleaseActivations",
                column: "ReleaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HmclReleaseActivations");
        }
    }
}
