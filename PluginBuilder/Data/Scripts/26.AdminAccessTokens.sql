CREATE TABLE admin_access_tokens (
    id uuid PRIMARY KEY,
    user_id text NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    name text NOT NULL,
    token_hash text NOT NULL UNIQUE,
    security_stamp text,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    last_used_at timestamptz
);
CREATE INDEX admin_access_tokens_owner_idx ON admin_access_tokens(user_id, created_at);
-- No user/token foreign keys: audit records survive account/token deletion.
CREATE TABLE admin_api_audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id text NOT NULL,
    token_id uuid,
    method text NOT NULL,
    path text NOT NULL,
    started_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    completed_at timestamptz,
    status_code integer
);
CREATE INDEX admin_api_audit_token_idx ON admin_api_audit(token_id, id DESC);
ALTER TABLE plugin_listing_requests ADD COLUMN reviewed_by_token uuid;
CREATE OR REPLACE FUNCTION admin_listing_review_event() RETURNS trigger AS $$
BEGIN
    IF OLD.status = 'pending' AND NEW.status IN ('approved', 'rejected') THEN
        PERFORM emit_admin_event('listing.' || NEW.status, jsonb_build_object('userId', NEW.reviewed_by,
            'tokenId', NEW.reviewed_by_token, 'pluginSlug', NEW.plugin_slug, 'listingRequestId', NEW.id,
            'reviewNote', NEW.review_note, 'rejectionReason', NEW.rejection_reason));
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
