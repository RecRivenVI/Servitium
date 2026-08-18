using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeSettingsAndScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RuntimeSettingsJson",
                table: "LocalInstances",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "LocalScheduledTaskRuns",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    OutcomeCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActionResultsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalScheduledTaskRuns", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "LocalScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemainingRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    OverlapPolicy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalScheduledTaskRuns_TaskId_Sequence",
                table: "LocalScheduledTaskRuns",
                columns: new[] { "TaskId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalScheduledTasks_InstanceId_Name",
                table: "LocalScheduledTasks",
                columns: new[] { "InstanceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalScheduledTasks_NextRunAt",
                table: "LocalScheduledTasks",
                column: "NextRunAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalScheduledTaskRuns");

            migrationBuilder.DropTable(
                name: "LocalScheduledTasks");

            migrationBuilder.DropColumn(
                name: "RuntimeSettingsJson",
                table: "LocalInstances");
        }
    }
}
