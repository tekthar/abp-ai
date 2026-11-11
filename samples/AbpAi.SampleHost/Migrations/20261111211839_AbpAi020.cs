using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AbpAi.SampleHost.Migrations
{
    /// <inheritdoc />
    public partial class AbpAi020 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.CreateTable(
                name: "aliases",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Targets = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aliases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "connections",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ProtectedKey = table.Column<string>(type: "text", nullable: true),
                    Models = table.Column<string>(type: "jsonb", nullable: false),
                    DataClasses = table.Column<string>(type: "jsonb", nullable: false),
                    Labels = table.Column<string>(type: "jsonb", nullable: false),
                    Settings = table.Column<string>(type: "jsonb", nullable: false),
                    InputPrice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    OutputPrice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ModelPrices = table.Column<string>(type: "jsonb", nullable: false),
                    StructuredOutput = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "limits",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_limits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "prompts",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    System = table.Column<string>(type: "text", nullable: true),
                    Messages = table.Column<string>(type: "jsonb", nullable: false),
                    Reply = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "site",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopesMayConnect = table.Column<bool>(type: "boolean", nullable: false),
                    KeepPrompts = table.Column<bool>(type: "boolean", nullable: false),
                    PromptDays = table.Column<int>(type: "integer", nullable: false),
                    ScopeDataClasses = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usage",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopePath = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    OnBehalfOf = table.Column<Guid>(type: "uuid", nullable: true),
                    Purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AboutKind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AboutId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Connection = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DataClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Streamed = table.Column<bool>(type: "boolean", nullable: false),
                    Reserved = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_aliases_ScopeId_Name",
                schema: "ai",
                table: "aliases",
                columns: new[] { "ScopeId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_connections_ScopeId_Name",
                schema: "ai",
                table: "connections",
                columns: new[] { "ScopeId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_limits_ScopeId_Kind_Period_AccountId_GroupId",
                schema: "ai",
                table: "limits",
                columns: new[] { "ScopeId", "Kind", "Period", "AccountId", "GroupId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_prompts_At",
                schema: "ai",
                table: "prompts",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_usage_AccountId_At",
                schema: "ai",
                table: "usage",
                columns: new[] { "AccountId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_At",
                schema: "ai",
                table: "usage",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_usage_OnBehalfOf_At",
                schema: "ai",
                table: "usage",
                columns: new[] { "OnBehalfOf", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_ScopePath",
                schema: "ai",
                table: "usage",
                column: "ScopePath")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_usage_State_At",
                schema: "ai",
                table: "usage",
                columns: new[] { "State", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aliases",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "connections",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "limits",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "prompts",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "site",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "usage",
                schema: "ai");
        }
    }
}
