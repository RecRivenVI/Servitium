using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddInstancePrimaryPort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PrimaryPort",
                table: "LocalInstances",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrimaryPort",
                table: "LocalInstances");
        }
    }
}
