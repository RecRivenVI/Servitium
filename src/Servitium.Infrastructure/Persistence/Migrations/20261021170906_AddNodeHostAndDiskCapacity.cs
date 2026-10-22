using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeHostAndDiskCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostName",
                table: "Nodes",
                type: "TEXT",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocal",
                table: "Nodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DiskTotalBytes",
                table: "NodeMetrics",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HostName",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "IsLocal",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "DiskTotalBytes",
                table: "NodeMetrics");
        }
    }
}
