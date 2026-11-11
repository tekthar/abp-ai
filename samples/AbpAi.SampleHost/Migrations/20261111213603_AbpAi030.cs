using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AbpAi.SampleHost.Migrations
{
    /// <inheritdoc />
    public partial class AbpAi030 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScopeAccess",
                schema: "ai",
                table: "site",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "open");

            migrationBuilder.AddColumn<string>(
                name: "ModelSettings",
                schema: "ai",
                table: "connections",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.CreateTable(
                name: "scope_access",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllSiteModels = table.Column<bool>(type: "boolean", nullable: false),
                    SiteModels = table.Column<string>(type: "jsonb", nullable: false),
                    MayConnect = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scope_access", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scope_access",
                schema: "ai");

            migrationBuilder.DropColumn(
                name: "ScopeAccess",
                schema: "ai",
                table: "site");

            migrationBuilder.DropColumn(
                name: "ModelSettings",
                schema: "ai",
                table: "connections");
        }
    }
}
