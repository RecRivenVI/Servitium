using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NodeRegistrationTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpectedName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeRegistrationTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProtocolMajor = table.Column<uint>(type: "INTEGER", nullable: false),
                    ProtocolMinor = table.Column<uint>(type: "INTEGER", nullable: false),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Architecture = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MachineFingerprint = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CredentialRevoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeCredentials",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeCredentials_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NodeCredentials_CredentialHash",
                table: "NodeCredentials",
                column: "CredentialHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NodeCredentials_NodeId_Revision",
                table: "NodeCredentials",
                columns: new[] { "NodeId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NodeRegistrationTokens_ExpiresAt",
                table: "NodeRegistrationTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_NodeRegistrationTokens_TokenHash",
                table: "NodeRegistrationTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_MachineFingerprint",
                table: "Nodes",
                column: "MachineFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Name",
                table: "Nodes",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NodeCredentials");

            migrationBuilder.DropTable(
                name: "NodeRegistrationTokens");

            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}
