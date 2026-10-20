using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddJavaVmName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VmName",
                table: "LocalJavaRuntimes",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VmName",
                table: "LocalJavaRuntimes");
        }
    }
}
