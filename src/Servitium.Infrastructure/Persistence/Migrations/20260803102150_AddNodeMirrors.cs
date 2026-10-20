using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeMirrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NodeInstanceMirrors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeInstanceMirrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeInstanceMirrors_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeJavaRuntimeMirrors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeJavaRuntimeMirrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeJavaRuntimeMirrors_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeMirrorStates",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeMirrorStates", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_NodeMirrorStates_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeScheduleMirrors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeScheduleMirrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeScheduleMirrors_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NodeInstanceMirrors_NodeId",
                table: "NodeInstanceMirrors",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_NodeJavaRuntimeMirrors_NodeId",
                table: "NodeJavaRuntimeMirrors",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_NodeScheduleMirrors_NodeId",
                table: "NodeScheduleMirrors",
                column: "NodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NodeInstanceMirrors");

            migrationBuilder.DropTable(
                name: "NodeJavaRuntimeMirrors");

            migrationBuilder.DropTable(
                name: "NodeMirrorStates");

            migrationBuilder.DropTable(
                name: "NodeScheduleMirrors");
        }
    }
}
