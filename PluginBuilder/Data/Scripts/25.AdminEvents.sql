-- Separate the durable admin audit stream from download statistics in evts.
CREATE TABLE admin_event_counter (singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton), value bigint NOT NULL);
INSERT INTO admin_event_counter VALUES (TRUE, 0);
CREATE TABLE admin_events (
    id bigint PRIMARY KEY,
    type text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    data jsonb NOT NULL
);
CREATE INDEX admin_events_type_id_idx ON admin_events(type, id);
CREATE TABLE admin_event_subscriptions (
    id uuid PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('webhook', 'email')),
    destination text NOT NULL,
    protected_secret text CHECK (kind <> 'webhook' OR protected_secret IS NOT NULL),
    event_types text[] NOT NULL,
    enabled boolean NOT NULL DEFAULT TRUE,
    created_by text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE admin_event_deliveries (
    subscription_id uuid NOT NULL REFERENCES admin_event_subscriptions(id) ON DELETE CASCADE,
    event_id bigint NOT NULL REFERENCES admin_events(id),
    attempts integer NOT NULL DEFAULT 0,
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'delivered', 'failed')),
    next_attempt_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_error text,
    PRIMARY KEY(subscription_id, event_id)
);
CREATE INDEX admin_event_deliveries_pending_idx ON admin_event_deliveries(next_attempt_at) WHERE status = 'pending';

-- Updating a transactional counter serializes writers until commit. A polling
-- cursor cannot skip an event whose sequence was allocated before a later commit.
CREATE FUNCTION emit_admin_event(event_type text, event_data jsonb) RETURNS void AS $$
DECLARE event_id bigint;
BEGIN
    UPDATE admin_event_counter SET value = value + 1 RETURNING value INTO event_id;
    INSERT INTO admin_events(id, type, data) VALUES (event_id, event_type, event_data);
    INSERT INTO admin_event_deliveries(subscription_id, event_id)
        SELECT id, event_id FROM admin_event_subscriptions
        WHERE enabled AND (cardinality(event_types) = 0 OR event_type = ANY(event_types));
END;
$$ LANGUAGE plpgsql;

ALTER TABLE builds ADD COLUMN triggered_by text;
ALTER TABLE plugin_listing_requests ADD COLUMN submitted_by text;
ALTER TABLE plugin_listing_requests ADD COLUMN review_note text;
ALTER TABLE builds_logs ADD COLUMN id bigint;
-- Preserve historical timestamp order. ctid breaks ties between otherwise
-- identical legacy rows while this migration holds the table lock.
WITH ordered AS (
    SELECT ctid, row_number() OVER (ORDER BY created_at, plugin_slug, build_id, ctid) AS id FROM builds_logs
)
UPDATE builds_logs l SET id = ordered.id FROM ordered WHERE l.ctid = ordered.ctid;
ALTER TABLE builds_logs ALTER COLUMN id SET NOT NULL;
ALTER TABLE builds_logs ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY;
SELECT setval(pg_get_serial_sequence('builds_logs', 'id'), COALESCE((SELECT max(id) FROM builds_logs), 0) + 1, false);
CREATE UNIQUE INDEX builds_logs_id_idx ON builds_logs(id);
CREATE INDEX builds_logs_review_idx ON builds_logs(plugin_slug, build_id, id DESC);
CREATE TABLE admin_event_first_builds (user_id text PRIMARY KEY);
-- Historical builds have no actor. Existing owners with builds are conservatively
-- treated as established builders; no synthetic historical events are generated.
INSERT INTO admin_event_first_builds SELECT DISTINCT user_id FROM users_plugins up
WHERE EXISTS (SELECT 1 FROM builds b WHERE b.plugin_slug = up.plugin_slug);

CREATE FUNCTION admin_user_event() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        PERFORM emit_admin_event('user.registered', jsonb_build_object('userId', NEW."Id"));
    ELSIF NULLIF(NEW."GithubGistUrl", '') IS NOT NULL AND NEW."GithubGistUrl" IS DISTINCT FROM OLD."GithubGistUrl" THEN
        PERFORM emit_admin_event('user.github_verified', jsonb_build_object(
            'userId', NEW."Id", 'github', NEW."AccountDetail"->>'github', 'gistUrl', NEW."GithubGistUrl"));
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER admin_user_registered AFTER INSERT ON "AspNetUsers" FOR EACH ROW EXECUTE FUNCTION admin_user_event();
CREATE TRIGGER admin_user_verified AFTER UPDATE OF "GithubGistUrl" ON "AspNetUsers" FOR EACH ROW EXECUTE FUNCTION admin_user_event();

CREATE FUNCTION admin_build_event() RETURNS trigger AS $$
DECLARE payload jsonb;
BEGIN
    payload := jsonb_build_object('userId', NEW.triggered_by, 'pluginSlug', NEW.plugin_slug,
        'buildId', NEW.id, 'state', NEW.state, 'repository', NEW.build_info->>'gitRepository',
        'gitRef', NEW.build_info->>'gitRef', 'gitCommit', NEW.build_info->>'gitCommit',
        'pluginDirectory', NEW.build_info->>'pluginDir');
    IF TG_OP = 'INSERT' THEN
        PERFORM emit_admin_event('build.triggered', payload);
        IF NEW.triggered_by IS NOT NULL THEN
            INSERT INTO admin_event_first_builds VALUES (NEW.triggered_by) ON CONFLICT DO NOTHING;
            IF FOUND THEN
                PERFORM emit_admin_event('user.first_build_triggered', payload);
            END IF;
        END IF;
    ELSIF NEW.state IS DISTINCT FROM OLD.state AND NEW.state IN ('uploaded', 'failed') THEN
        PERFORM emit_admin_event(CASE WHEN NEW.state = 'uploaded' THEN 'build.succeeded' ELSE 'build.failed' END, payload);
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER admin_build_created AFTER INSERT ON builds FOR EACH ROW EXECUTE FUNCTION admin_build_event();
CREATE TRIGGER admin_build_finished AFTER UPDATE OF state ON builds FOR EACH ROW EXECUTE FUNCTION admin_build_event();

CREATE FUNCTION admin_listing_event() RETURNS trigger AS $$
BEGIN
    PERFORM emit_admin_event('listing.requested', jsonb_build_object('userId', NEW.submitted_by,
        'pluginSlug', NEW.plugin_slug, 'listingRequestId', NEW.id,
        'releaseNote', NEW.release_note, 'telegramVerificationMessage', NEW.telegram_verification_message,
        'userReviews', NEW.user_reviews, 'announcementDate', NEW.announcement_date));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER admin_listing_requested AFTER INSERT ON plugin_listing_requests FOR EACH ROW EXECUTE FUNCTION admin_listing_event();

CREATE FUNCTION admin_listing_review_event() RETURNS trigger AS $$
BEGIN
    IF OLD.status = 'pending' AND NEW.status IN ('approved', 'rejected') THEN
        PERFORM emit_admin_event('listing.' || NEW.status, jsonb_build_object('userId', NEW.reviewed_by,
            'pluginSlug', NEW.plugin_slug, 'listingRequestId', NEW.id, 'reviewNote', NEW.review_note,
            'rejectionReason', NEW.rejection_reason));
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER admin_listing_reviewed AFTER UPDATE OF status ON plugin_listing_requests FOR EACH ROW EXECUTE FUNCTION admin_listing_review_event();
