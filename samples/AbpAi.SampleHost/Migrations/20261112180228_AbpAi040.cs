using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AbpAi.SampleHost.Migrations
{
    /// <inheritdoc />
    public partial class AbpAi040 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_connections",
                schema: "ai",
                table: "connections");

            migrationBuilder.AddColumn<int>(
                name: "CacheReadTokens",
                schema: "ai",
                table: "usage",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CacheWriteTokens",
                schema: "ai",
                table: "usage",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                schema: "ai",
                table: "usage",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReachedNoticeFor",
                schema: "ai",
                table: "limits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CacheReadPrice",
                schema: "ai",
                table: "connections",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CacheWritePrice",
                schema: "ai",
                table: "connections",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdleTimeoutSeconds",
                schema: "ai",
                table: "connections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalTimeoutSeconds",
                schema: "ai",
                table: "connections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ai_connections",
                schema: "ai",
                table: "connections",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "exports",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    To = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    Prompts = table.Column<int>(type: "integer", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_exports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_exports_ExpiresAt",
                schema: "ai",
                table: "exports",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exports",
                schema: "ai");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ai_connections",
                schema: "ai",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                schema: "ai",
                table: "usage");

            migrationBuilder.DropColumn(
                name: "CacheWriteTokens",
                schema: "ai",
                table: "usage");

            migrationBuilder.DropColumn(
                name: "Code",
                schema: "ai",
                table: "usage");

            migrationBuilder.DropColumn(
                name: "ReachedNoticeFor",
                schema: "ai",
                table: "limits");

            migrationBuilder.DropColumn(
                name: "CacheReadPrice",
                schema: "ai",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "CacheWritePrice",
                schema: "ai",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "IdleTimeoutSeconds",
                schema: "ai",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "TotalTimeoutSeconds",
                schema: "ai",
                table: "connections");

            migrationBuilder.AddPrimaryKey(
                name: "PK_connections",
                schema: "ai",
                table: "connections",
                column: "Id");
        }
    }
}
