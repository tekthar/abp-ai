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
                name: "ModelSettings",
                schema: "ai",
                table: "connections",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModelSettings",
                schema: "ai",
                table: "connections");
        }
    }
}
