using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QbAutopost.Data.Migrations
{
    /// <summary>
    /// T-1106: the read-only view <c>audit_events</c> (spec saved-requests-dashboard-v2 8.3), a UNION ALL over the
    /// timeline events, the version change lists and <c>user_audit</c>. Nothing new is written. The rules are those of
    /// <c>QbAutopost.Core.SavedRequests.Audit.AuditEventMapper</c>, in the same order; change both together.
    /// </summary>
    public partial class AuditEventsView : Migration
    {
        /// <summary>The view's SQL. Enum values stored PascalCase in event details are output kebab-case.</summary>
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
                END AS detail
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
                jsonb_build_object('note', v.note) AS detail
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
                END AS detail
            FROM user_audit u;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateViewSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS audit_events;");
        }
    }
}
