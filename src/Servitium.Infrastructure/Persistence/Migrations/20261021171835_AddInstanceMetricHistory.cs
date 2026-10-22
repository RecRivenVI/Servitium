using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceMetricHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstanceMetrics",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Minute = table.Column<long>(type: "INTEGER", nullable: false),
                    SampledAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                    MemoryBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    OnlinePlayers = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceMetrics", x => new { x.NodeId, x.InstanceId, x.Minute });
                    table.ForeignKey(
                        name: "FK_InstanceMetrics_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceMetrics_SampledAtUnixMilliseconds",
                table: "InstanceMetrics",
                column: "SampledAtUnixMilliseconds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceMetrics");
        }
    }
}
