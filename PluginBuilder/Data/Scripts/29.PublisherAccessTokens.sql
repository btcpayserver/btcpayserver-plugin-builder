-- Personal API tokens for publishers: each belongs to one user and one plugin, and reaches that plugin's API routes only.
-- Only a hash of the token is kept. security_stamp is the user's stamp at creation, so a password change (or anything
-- else that rotates the stamp) ends the user's tokens, as it does for admin access tokens.
CREATE TABLE publisher_access_tokens (
    id uuid PRIMARY KEY,
    user_id text NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    plugin_slug text NOT NULL REFERENCES plugins(slug) ON DELETE CASCADE,
    name text NOT NULL,
    token_hash text NOT NULL UNIQUE,
    security_stamp text,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at timestamptz,
    revoked_at timestamptz,
    last_used_at timestamptz
);
CREATE INDEX publisher_access_tokens_owner_idx ON publisher_access_tokens(user_id, created_at);
