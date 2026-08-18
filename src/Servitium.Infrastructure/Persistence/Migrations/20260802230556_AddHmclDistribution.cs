using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHmclDistribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HmclChannels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Visibility = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ActiveReleaseId = table.Column<string>(type: "TEXT", nullable: true),
                    RetentionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmclChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HmclContentObjects",
                columns: table => new
                {
                    Sha1 = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferenceCount = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmclContentObjects", x => x.Sha1);
                });

            migrationBuilder.CreateTable(
                name: "HmclReleases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    GameVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LoaderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LoaderVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OptiFineVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Manifest = table.Column<byte[]>(type: "BLOB", maxLength: 16777216, nullable: false),
                    ManifestSha1 = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmclReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HmclReleases_HmclChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "HmclChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HmclReleaseFiles",
                columns: table => new
                {
                    ReleaseId = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    ContentSha1 = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmclReleaseFiles", x => new { x.ReleaseId, x.Path });
                    table.ForeignKey(
                        name: "FK_HmclReleaseFiles_HmclReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "HmclReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HmclChannels_Slug",
                table: "HmclChannels",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HmclReleaseFiles_ContentSha1",
                table: "HmclReleaseFiles",
                column: "ContentSha1");

            migrationBuilder.CreateIndex(
                name: "IX_HmclReleases_ChannelId_Sequence",
                table: "HmclReleases",
                columns: new[] { "ChannelId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HmclContentObjects");

            migrationBuilder.DropTable(
                name: "HmclReleaseFiles");

            migrationBuilder.DropTable(
                name: "HmclReleases");

            migrationBuilder.DropTable(
                name: "HmclChannels");
        }
    }
}
