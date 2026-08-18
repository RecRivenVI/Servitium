using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredAuditDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OccurredAtUnixMilliseconds",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PropertiesJson",
                table: "AuditLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<long>(
                name: "OccurredAtUnixMilliseconds",
                table: "ActivityLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeCode",
                table: "ActivityLogs",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "success");

            migrationBuilder.AddColumn<string>(
                name: "SourceAddress",
                table: "ActivityLogs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OccurredAtUnixMilliseconds",
                table: "AuditLogs",
                column: "OccurredAtUnixMilliseconds");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_OccurredAtUnixMilliseconds",
                table: "ActivityLogs",
                column: "OccurredAtUnixMilliseconds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_OccurredAtUnixMilliseconds",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_ActivityLogs_OccurredAtUnixMilliseconds",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "OccurredAtUnixMilliseconds",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "PropertiesJson",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "OccurredAtUnixMilliseconds",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "OutcomeCode",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "SourceAddress",
                table: "ActivityLogs");
        }
    }
}
