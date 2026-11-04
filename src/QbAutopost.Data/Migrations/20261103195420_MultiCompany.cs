using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace QbAutopost.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiCompany : Migration
    {
        /// <summary>
        /// The audit_events view with a <c>company_id</c> column (M17 plan conflict 4): the T-1106 branches plus the company
        /// of the request, and a fourth branch (id prefix <c>c</c>) for the company kinds written to <c>user_audit</c>.
        /// Change <c>AuditEventMapper</c> with it.
        /// </summary>
        public const string CreateViewSql =
            """
            CREATE VIEW audit_events AS
            SELECT
                'e' || e.id AS id,
                e.at_utc AS at_utc,
                e.actor AS actor_id,
                CASE WHEN e.actor = 'system' THEN 'system'
                     WHEN e.actor = 'anonymous' THEN 'anonymous'
                     WHEN starts_with(e.actor, 'api-client:') THEN 'api-client'
                     ELSE 'user' END AS actor_kind,
                ea.action AS action,
                e.saved_request_id AS request_id,
                NULL::integer AS row_index,
                NULL::text AS row_external_id,
                NULL::text AS field,
                NULL::jsonb AS old,
                NULL::jsonb AS new,
                CASE WHEN e.kind IN ('validated', 'post-started') THEN (e.detail ->> 'version')::integer END AS version_number,
                CASE ea.action
                    WHEN 'validated' THEN jsonb_build_object(
                        'state', lower(regexp_replace(e.detail ->> 'state', '([a-z0-9])([A-Z])', '\1-\2', 'g')))
                    WHEN 'post-started' THEN jsonb_build_object('attemptId', e.detail -> 'attemptId')
                    WHEN 'posted' THEN jsonb_build_object('attemptId', e.detail -> 'attemptId', 'postId', e.detail -> 'postId')
                    WHEN 'post-failed' THEN jsonb_build_object(
                        'attemptId', e.detail -> 'attemptId',
                        'outcome', lower(regexp_replace(e.detail ->> 'outcome', '([a-z0-9])([A-Z])', '\1-\2', 'g')),
                        'status', lower(regexp_replace(e.detail ->> 'status', '([a-z0-9])([A-Z])', '\1-\2', 'g')),
                        'postId', e.detail -> 'postId')
                    WHEN 'resolved' THEN jsonb_build_object(
                        'outcome', lower(regexp_replace(e.detail ->> 'outcome', '([a-z0-9])([A-Z])', '\1-\2', 'g')),
                        'note', e.detail -> 'note',
                        'from', lower(regexp_replace(e.detail ->> 'from', '([a-z0-9])([A-Z])', '\1-\2', 'g')))
                    ELSE e.detail
                END AS detail,
                (SELECT r.company_id FROM saved_requests r WHERE r.id = e.saved_request_id) AS company_id
            FROM saved_request_events e
            CROSS JOIN LATERAL (SELECT CASE e.kind
                WHEN 'created' THEN 'received'
                WHEN 'post-finished' THEN
                    CASE WHEN lower(regexp_replace(e.detail ->> 'outcome', '([a-z0-9])([A-Z])', '\1-\2', 'g')) = 'posted'
                         THEN 'posted' ELSE 'post-failed' END
                ELSE e.kind END AS action) ea
            WHERE e.kind <> 'edited'

            UNION ALL

            SELECT
                'v' || v.id || '-' || (c.ord - 1) AS id,
                v.created_utc AS at_utc,
                v.created_by AS actor_id,
                CASE WHEN v.created_by = 'system' THEN 'system'
                     WHEN v.created_by = 'anonymous' THEN 'anonymous'
                     WHEN starts_with(v.created_by, 'api-client:') THEN 'api-client'
                     ELSE 'user' END AS actor_kind,
                va.action AS action,
                v.saved_request_id AS request_id,
                CASE WHEN va.is_row THEN ch.row_index END AS row_index,
                CASE WHEN va.action = 'row-deleted' THEN p.payload -> 'transactions' -> ch.row_index ->> 'externalId'
                     WHEN va.is_row THEN v.payload -> 'transactions' -> ch.row_index ->> 'externalId' END AS row_external_id,
                CASE va.action
                    WHEN 'row-post-off' THEN 'post'
                    WHEN 'row-post-on' THEN 'post'
                    WHEN 'row-edited' THEN regexp_replace(ch.rest, '^\.', '')
                    WHEN 'control-total-changed' THEN 'controlTotal'
                    WHEN 'request-edited' THEN NULLIF(regexp_replace(coalesce(ch.path, ''), '^\$\.?', ''), '')
                END AS field,
                ch.old AS old,
                ch.new AS new,
                v.number AS version_number,
                jsonb_build_object('note', v.note) AS detail,
                (SELECT r.company_id FROM saved_requests r WHERE r.id = v.saved_request_id) AS company_id
            FROM saved_request_versions v
            LEFT JOIN saved_request_versions p ON p.saved_request_id = v.saved_request_id AND p.number = v.number - 1
            CROSS JOIN LATERAL jsonb_array_elements(v.changes) WITH ORDINALITY AS c(change, ord)
            CROSS JOIN LATERAL (SELECT
                c.change ->> 'op' AS op,
                c.change ->> 'path' AS path,
                NULLIF(c.change -> 'old', 'null'::jsonb) AS old,
                NULLIF(c.change -> 'new', 'null'::jsonb) AS new,
                substring(c.change ->> 'path' from '^\$\.transactions\[([0-9]+)\]')::integer AS row_index,
                substring(c.change ->> 'path' from '^\$\.transactions\[[0-9]+\](.*)$') AS rest) ch
            CROSS JOIN LATERAL (SELECT r.action, r.action LIKE 'row-%' AS is_row FROM (SELECT CASE
                WHEN ch.rest = '' AND ch.op = 'add' THEN 'row-added'
                WHEN ch.rest = '' AND ch.op = 'remove' THEN 'row-deleted'
                WHEN ch.rest = '.post' THEN CASE WHEN ch.new = 'false'::jsonb THEN 'row-post-off' ELSE 'row-post-on' END
                WHEN ch.rest <> '' THEN 'row-edited'
                WHEN ch.path = '$.controlTotal' THEN 'control-total-changed'
                ELSE 'request-edited' END AS action) r) va

            UNION ALL

            SELECT
                'u' || u.id AS id,
                u.at_utc AS at_utc,
                u.actor AS actor_id,
                CASE WHEN u.actor = 'system' THEN 'system'
                     WHEN u.actor = 'anonymous' THEN 'anonymous'
                     WHEN starts_with(u.actor, 'api-client:') THEN 'api-client'
                     ELSE 'user' END AS actor_kind,
                CASE u.kind
                    WHEN 'created' THEN 'user-created'
                    WHEN 'role-changed' THEN 'user-role-changed'
                    WHEN 'disabled' THEN 'user-disabled'
                    WHEN 'enabled' THEN 'user-enabled'
                    WHEN 'password-reset' THEN 'user-password-reset'
                    WHEN 'unlocked' THEN 'user-unlocked'
                    WHEN 'password-changed' THEN 'user-password-changed'
                    WHEN 'login' THEN 'user-signed-in'
                    WHEN 'login-failed' THEN 'user-sign-in-failed'
                    WHEN 'locked-out' THEN 'user-locked-out'
                    WHEN 'refresh-reuse' THEN 'user-session-revoked'
                    ELSE 'user-other' END AS action,
                NULL::bigint AS request_id,
                NULL::integer AS row_index,
                NULL::text AS row_external_id,
                NULL::text AS field,
                NULL::jsonb AS old,
                NULL::jsonb AS new,
                NULL::integer AS version_number,
                CASE
                    WHEN u.kind = 'refresh-reuse' THEN jsonb_build_object('familyId', u.detail -> 'familyId')
                    WHEN u.kind IN ('created', 'role-changed', 'disabled', 'enabled', 'password-reset', 'unlocked',
                                    'password-changed', 'login', 'login-failed', 'locked-out')
                        THEN (CASE WHEN jsonb_typeof(u.detail) = 'object' THEN u.detail ELSE '{}'::jsonb END)
                             || jsonb_build_object('userId', u.user_id)
                    ELSE jsonb_build_object('kind', u.kind)
                END AS detail,
                NULL::integer AS company_id
            FROM user_audit u
            WHERE u.kind NOT IN ('companies-scanned', 'company-updated', 'company-checked')

            UNION ALL

            SELECT
                'c' || u.id AS id,
                u.at_utc AS at_utc,
                u.actor AS actor_id,
                CASE WHEN u.actor = 'system' THEN 'system'
                     WHEN u.actor = 'anonymous' THEN 'anonymous'
                     WHEN starts_with(u.actor, 'api-client:') THEN 'api-client'
                     ELSE 'user' END AS actor_kind,
                u.kind AS action,
                NULL::bigint AS request_id,
                NULL::integer AS row_index,
                NULL::text AS row_external_id,
                NULL::text AS field,
                NULL::jsonb AS old,
                NULL::jsonb AS new,
                NULL::integer AS version_number,
                u.detail AS detail,
                (u.detail ->> 'companyId')::integer AS company_id
            FROM user_audit u
            WHERE u.kind IN ('companies-scanned', 'company-updated', 'company-checked');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "company_id",
                table: "statement_extractions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_match",
                table: "statement_extractions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "company_id",
                table: "saved_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_match",
                table: "saved_requests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_source",
                table: "saved_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "company_id",
                table: "qb_list_syncs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    folder_name = table.Column<string>(type: "text", nullable: true),
                    folder_key = table.Column<string>(type: "text", nullable: true, computedColumnSql: "lower(folder_name)", stored: true),
                    code = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: true),
                    candidate_files = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    access = table.Column<string>(type: "text", nullable: false),
                    access_checked_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    access_message = table.Column<string>(type: "text", nullable: true),
                    qb_company_name = table.Column<string>(type: "text", nullable: true),
                    qb_legal_name = table.Column<string>(type: "text", nullable: true),
                    match_names = table.Column<string>(type: "jsonb", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_statement_extractions_company_id",
                table: "statement_extractions",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_requests_company_id_status",
                table: "saved_requests",
                columns: new[] { "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_qb_list_syncs_company_id_outcome_id",
                table: "qb_list_syncs",
                columns: new[] { "company_id", "outcome", "id" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_companies_folder_name",
                table: "companies",
                column: "folder_key",
                unique: true,
                filter: "folder_name IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_companies_is_default",
                table: "companies",
                column: "is_default",
                unique: true,
                filter: "is_default");

            migrationBuilder.AddForeignKey(
                name: "fk_qb_list_syncs_companies_company_id",
                table: "qb_list_syncs",
                column: "company_id",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_saved_requests_companies_company_id",
                table: "saved_requests",
                column: "company_id",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_statement_extractions_companies_company_id",
                table: "statement_extractions",
                column: "company_id",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Hand-added: the view gains company_id (and the company events branch). A view's columns cannot be
            // changed in place, and nothing else depends on it.
            migrationBuilder.Sql("DROP VIEW audit_events;");
            migrationBuilder.Sql(CreateViewSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The new view reads saved_requests.company_id, so it goes first; the T-1106 view is restored verbatim.
            migrationBuilder.Sql("DROP VIEW audit_events;");
            migrationBuilder.Sql(AuditEventsView.CreateViewSql);

            migrationBuilder.DropForeignKey(
                name: "fk_qb_list_syncs_companies_company_id",
                table: "qb_list_syncs");

            migrationBuilder.DropForeignKey(
                name: "fk_saved_requests_companies_company_id",
                table: "saved_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_statement_extractions_companies_company_id",
                table: "statement_extractions");

            migrationBuilder.DropTable(
                name: "companies");

            migrationBuilder.DropIndex(
                name: "ix_statement_extractions_company_id",
                table: "statement_extractions");

            migrationBuilder.DropIndex(
                name: "ix_saved_requests_company_id_status",
                table: "saved_requests");

            migrationBuilder.DropIndex(
                name: "ix_qb_list_syncs_company_id_outcome_id",
                table: "qb_list_syncs");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "statement_extractions");

            migrationBuilder.DropColumn(
                name: "company_match",
                table: "statement_extractions");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "company_match",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "company_source",
                table: "saved_requests");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "qb_list_syncs");
        }
    }
}
