CREATE INDEX aspnetusers_github_user_id_idx ON "AspNetUsers" ("GithubUserId")
    WHERE "GithubUserId" IS NOT NULL;

-- Numeric identities survive login changes. Revocation is explicit and does not unlock accounts.
CREATE TABLE github_bans (
    github_user_id bigint PRIMARY KEY CHECK (github_user_id > 0),
    github_login text NOT NULL,
    reason text NOT NULL,
    banned_by text,
    banned_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
