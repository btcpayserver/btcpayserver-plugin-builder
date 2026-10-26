-- ID bans survive renames; username bans are a fallback for verified legacy accounts.
CREATE TABLE github_bans (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    github_user_id bigint UNIQUE CHECK (github_user_id > 0),
    github_login text NOT NULL,
    reason text NOT NULL,
    banned_by text,
    banned_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (github_user_id IS NOT NULL OR length(trim(github_login)) > 0)
);
CREATE UNIQUE INDEX github_bans_legacy_login_idx ON github_bans (lower(github_login))
    WHERE github_user_id IS NULL;
