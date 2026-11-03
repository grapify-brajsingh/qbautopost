-- T-1016: what the application role may do (spec 5.4). Run as the owner (or a superuser) in database qbautopost,
-- AFTER every migration run, because a GRANT covers only the tables that exist when it runs.
-- Shared by deploy/install-postgres.ps1, deploy/apply-migrations.ps1 and tests/QbAutopost.Data.Tests/MigrationScriptTests.cs.
-- Safe to run again. Role qbautopost_app must exist. (CONNECT on the database is granted by install-postgres.ps1.)
--
--   * No DDL, no TRUNCATE, no DELETE ... except user_roles (see the end: changing a user's role replaces the row),
--     statement_extractions (expired uploads are removed) and qb_list_syncs (history retention, M14).
--   * saved_request_versions, saved_request_events and user_audit are append-only: SELECT and INSERT, nothing else.
--   * __EFMigrationsHistory is read-only (the app only compares it, spec 5.5).
--   * The view audit_events is read-only (SELECT).

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM qbautopost_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM qbautopost_app;

GRANT USAGE ON SCHEMA public TO qbautopost_app;

-- Mutable tables. T-1105: the DashboardV2 columns on saved_requests and refresh_tokens.grace_successor_id
-- are covered by these table grants (the migration adds no table, so nothing new to grant).
GRANT SELECT, INSERT, UPDATE ON
    saved_requests,
    post_attempts,
    refresh_tokens,
    users,
    roles,
    user_claims,
    user_logins,
    user_tokens,
    role_claims,
    user_roles
TO qbautopost_app;

-- Append-only tables.
GRANT SELECT, INSERT ON
    saved_request_versions,
    saved_request_events,
    user_audit
TO qbautopost_app;

-- T-1209: extraction records (dashboard-api-m12 6.6). The only table the app may DELETE from besides user_roles:
-- unsaved, expired records are removed in-line; saved ones are kept (the statement's same-file check needs them).
GRANT SELECT, INSERT, UPDATE, DELETE ON statement_extractions TO qbautopost_app;

-- T-1403: list sync history and the current QuickBooks lists (M14 spec 3). DELETE only for retention: each insert
-- prunes the rows beyond the newest HistoryRows (never the newest ok row) in the same transaction. Rows are never updated.
GRANT SELECT, INSERT, DELETE ON qb_list_syncs TO qbautopost_app;

-- Migration history: read only.
GRANT SELECT ON "__EFMigrationsHistory" TO qbautopost_app;

-- T-1106: the audit feed view (spec saved-requests-dashboard-v2 8.3). SELECT only: it is never written.
GRANT SELECT ON audit_events TO qbautopost_app;

-- Identity columns are GENERATED ... AS IDENTITY and need no grant; this covers any sequence a later migration adds.
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO qbautopost_app;

-- The one DELETE: UserEndpoints.ReplaceRoleAsync removes a user's old role row when an admin changes the role.
-- user_roles holds no history (user_audit records the change), so this loses nothing.
GRANT DELETE ON user_roles TO qbautopost_app;
