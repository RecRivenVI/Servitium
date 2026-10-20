using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastExitCode",
                table: "LocalInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProcessId",
                table: "LocalInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "LocalInstances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastExitCode",
                table: "LocalInstances");

            migrationBuilder.DropColumn(
                name: "ProcessId",
                table: "LocalInstances");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "LocalInstances");
        }
    }
}
