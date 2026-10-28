using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QbAutopost.Data.Migrations
{
    /// <inheritdoc />
    public partial class DashboardM12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "upload_extracted_utc",
                table: "saved_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "upload_extraction_id",
                table: "saved_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_file_name",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_model",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_prompt_version",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_sha256",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "upload_size_bytes",
                table: "saved_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "statement_extractions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    extracted_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    saved_request_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_extractions", x => x.id);
                    table.ForeignKey(
                        name: "fk_statement_extractions_saved_requests_saved_request_id",
                        column: x => x.saved_request_id,
                        principalTable: "saved_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_saved_requests_upload_sha256",
                table: "saved_requests",
                column: "upload_sha256");

            migrationBuilder.CreateIndex(
                name: "ix_saved_request_events_kind_at",
                table: "saved_request_events",
                columns: new[] { "kind", "at_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_statement_extractions_expires",
                table: "statement_extractions",
                column: "expires_utc",
                filter: "saved_request_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_statement_extractions_saved_request_id",
                table: "statement_extractions",
                column: "saved_request_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "statement_extractions");

            migrationBuilder.DropIndex(
                name: "ix_saved_requests_upload_sha256",
                table: "saved_requests");

            migrationBuilder.DropIndex(
                name: "ix_saved_request_events_kind_at",
                table: "saved_request_events");

            migrationBuilder.DropColumn(
                name: "upload_extracted_utc",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "upload_extraction_id",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "upload_file_name",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "upload_model",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "upload_prompt_version",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "upload_sha256",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "upload_size_bytes",
                table: "saved_requests");
        }
    }
}
