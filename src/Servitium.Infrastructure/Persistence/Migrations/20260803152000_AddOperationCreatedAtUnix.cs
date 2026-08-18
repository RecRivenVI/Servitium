using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servitium.Infrastructure.Persistence.Migrations;

/// <summary>为 SQLite 可排序的操作创建时间增加 Unix 毫秒列。</summary>
[DbContext(typeof(ServitiumDbContext))]
[Migration("20260803152000_AddOperationCreatedAtUnix")]
public sealed class AddOperationCreatedAtUnix : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Operations_CreatedAt",
            table: "Operations");

        migrationBuilder.AddColumn<long>(
            name: "CreatedAtUnixMilliseconds",
            table: "Operations",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.Sql(
            "UPDATE Operations SET CreatedAtUnixMilliseconds = CAST((julianday(CreatedAt) - 2440587.5) * 86400000 AS INTEGER);");

        migrationBuilder.CreateIndex(
            name: "IX_Operations_CreatedAtUnixMilliseconds",
            table: "Operations",
            column: "CreatedAtUnixMilliseconds");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Operations_CreatedAtUnixMilliseconds",
            table: "Operations");

        migrationBuilder.DropColumn(
            name: "CreatedAtUnixMilliseconds",
            table: "Operations");

        migrationBuilder.CreateIndex(
            name: "IX_Operations_CreatedAt",
            table: "Operations",
            column: "CreatedAt");
    }
}
