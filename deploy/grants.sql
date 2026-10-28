-- T-1016: what the application role may do (spec 5.4). Run as the owner (or a superuser) in database qbautopost,
-- AFTER every migration run, because a GRANT covers only the tables that exist when it runs.
-- Shared by deploy/install-postgres.ps1, deploy/apply-migrations.ps1 and tests/QbAutopost.Data.Tests/MigrationScriptTests.cs.
-- Safe to run again. Role qbautopost_app must exist. (CONNECT on the database is granted by install-postgres.ps1.)
--
--   * No DDL, no TRUNCATE, no DELETE ... except user_roles (see the end: changing a user's role replaces the row).
--   * saved_request_versions, saved_request_events and user_audit are append-only: SELECT and INSERT, nothing else.
--   * __EFMigrationsHistory is read-only (the app only compares it, spec 5.5).

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM qbautopost_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM qbautopost_app;

GRANT USAGE ON SCHEMA public TO qbautopost_app;

-- Mutable tables.
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

-- Migration history: read only.
GRANT SELECT ON "__EFMigrationsHistory" TO qbautopost_app;

-- Identity columns are GENERATED ... AS IDENTITY and need no grant; this covers any sequence a later migration adds.
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO qbautopost_app;

-- The one DELETE: UserEndpoints.ReplaceRoleAsync removes a user's old role row when an admin changes the role.
-- user_roles holds no history (user_audit records the change), so this loses nothing.
GRANT DELETE ON user_roles TO qbautopost_app;
