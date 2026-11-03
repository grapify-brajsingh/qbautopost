using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace QbAutopost.Data.Migrations
{
    /// <inheritdoc />
    public partial class QbListSyncs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "qb_list_syncs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: true),
                    started_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    accounts = table.Column<int>(type: "integer", nullable: true),
                    vendors = table.Column<int>(type: "integer", nullable: true),
                    customers = table.Column<int>(type: "integer", nullable: true),
                    missing_in_rules = table.Column<string>(type: "jsonb", nullable: true),
                    lists = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qb_list_syncs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_qb_list_syncs_outcome_id",
                table: "qb_list_syncs",
                columns: new[] { "outcome", "id" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qb_list_syncs");
        }
    }
}
