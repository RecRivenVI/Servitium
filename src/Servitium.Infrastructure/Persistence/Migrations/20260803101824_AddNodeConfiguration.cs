using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfigurationJson",
                table: "Nodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigurationJson",
                table: "Nodes");
        }
    }
}
