using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QbAutopost.Data.Migrations
{
    /// <inheritdoc />
    public partial class DashboardV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email_from",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_message_id",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "email_received_utc",
                table: "saved_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_subject",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "metadata_errors",
                table: "saved_requests",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "needs_attention_v2",
                table: "saved_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "problem_count",
                table: "saved_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "push_id",
                table: "saved_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "skip_count",
                table: "saved_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "validation_v2",
                table: "saved_requests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "grace_successor_id",
                table: "refresh_tokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "push_id_hex",
                table: "saved_requests",
                type: "text",
                nullable: true,
                computedColumnSql: "replace(push_id::text, '-', '')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_audit_at_utc",
                table: "user_audit",
                column: "at_utc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_saved_requests_push_id_hex",
                table: "saved_requests",
                column: "push_id_hex")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ux_saved_requests_push_id",
                table: "saved_requests",
                column: "push_id",
                unique: true,
                filter: "push_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_saved_request_versions_created_utc",
                table: "saved_request_versions",
                column: "created_utc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_saved_request_events_at_utc",
                table: "saved_request_events",
                column: "at_utc",
                descending: new bool[0]);

            // Backfill (spec saved-requests-dashboard-v2 4.2): no stored payload has a post flag or a pushId yet, so
            // the v2 judgement of every existing request is its v1 judgement.
            migrationBuilder.Sql(
                """
                UPDATE saved_requests SET validation_v2 = validation, needs_attention_v2 = needs_attention,
                  problem_count = coalesce(jsonb_array_length(validation->'validation'),0) + coalesce(jsonb_array_length(validation->'errors'),0);
                """);

            // skip_count from the current version's payload: rows whose post is the JSON literal false (0 everywhere today).
            migrationBuilder.Sql(
                """
                UPDATE saved_requests s SET skip_count = (
                  SELECT count(*) FROM saved_request_versions v
                  CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(v.payload->'transactions') = 'array' THEN v.payload->'transactions' ELSE '[]'::jsonb END) t
                  WHERE v.saved_request_id = s.id AND v.number = s.current_version AND t->'post' = 'false'::jsonb);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_audit_at_utc",
                table: "user_audit");

            migrationBuilder.DropIndex(
                name: "ix_saved_requests_push_id_hex",
                table: "saved_requests");

            migrationBuilder.DropIndex(
                name: "ux_saved_requests_push_id",
                table: "saved_requests");

            migrationBuilder.DropIndex(
                name: "ix_saved_request_versions_created_utc",
                table: "saved_request_versions");

            migrationBuilder.DropIndex(
                name: "ix_saved_request_events_at_utc",
                table: "saved_request_events");

            migrationBuilder.DropColumn(
                name: "push_id_hex",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "email_from",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "email_message_id",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "email_received_utc",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "email_subject",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "metadata_errors",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "needs_attention_v2",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "problem_count",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "push_id",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "skip_count",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "validation_v2",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "grace_successor_id",
                table: "refresh_tokens");
        }
    }
}
