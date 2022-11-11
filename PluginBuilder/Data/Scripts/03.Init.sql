INSERT INTO "AspNetRoles" VALUES ('5ba004fa-2e7a-42a7-b310-c72e719b3c19', 'ServerAdmin', 'SERVERADMIN', '');

CREATE TABLE plugins
(
    slug TEXT NOT NULL PRIMARY KEY
);

CREATE TABLE users_plugins
(
    user_id TEXT NOT NULL,
    plugin_slug TEXT NOT NULL,
    PRIMARY KEY(user_id, plugin_slug),
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
);


CREATE TABLE builds_ids
(
    plugin_slug TEXT NOT NULL,
    curr_id BIGINT NOT NULL,
    PRIMARY KEY (plugin_slug),
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE
);

CREATE TABLE builds
(
    plugin_slug TEXT NOT NULL,
    id BIGINT NOT NULL,
    state TEXT NOT NULL,
    manifest_info JSONB,
    build_info JSONB,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    /*PRIMARY KEY (plugin_slug, id),*/
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE
);
CREATE UNIQUE INDEX builds_pkey ON builds (plugin_slug, id DESC);

CREATE TABLE versions
(
    plugin_slug TEXT NOT NULL,
    ver INT[] NOT NULL,
    build_id BIGINT NOT NULL,
    btcpay_min_ver INT[] NOT NULL,
    pre_release BOOLEAN NOT NULL,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (plugin_slug, ver),
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE,
    FOREIGN KEY (plugin_slug, build_id) REFERENCES builds (plugin_slug, id) ON DELETE CASCADE
);

CREATE INDEX btcpay_min_ver_idx ON versions (btcpay_min_ver);

CREATE OR REPLACE FUNCTION versions_updating() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END
$$;
CREATE TRIGGER versions_update_trigger AFTER UPDATE ON versions FOR EACH ROW EXECUTE PROCEDURE versions_updating();

CREATE TABLE builds_logs
(
    plugin_slug TEXT NOT NULL,
    build_id BIGINT NOT NULL,
    logs TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE,
    FOREIGN KEY (plugin_slug, build_id) REFERENCES builds (plugin_slug, id) ON DELETE CASCADE
);
CREATE INDEX builds_logs_idx ON builds_logs (plugin_slug, build_id, created_at);


CREATE OR REPLACE FUNCTION get_latest_versions (btcpayVersion INT[], includePreRelease BOOLEAN)
RETURNS TABLE(plugin_slug TEXT, ver INT[], build_id BIGINT)
AS $$
WITH latest_versions AS
(
    SELECT plugin_slug, MAX(ver) ver FROM versions
    WHERE btcpay_min_ver <= btcpayVersion AND (includePreRelease OR pre_release IS FALSE)
    GROUP BY plugin_slug
)
SELECT v.plugin_slug, v.ver, v.build_id FROM latest_versions lv
JOIN versions v USING (plugin_slug, ver)

$$ LANGUAGE SQL STABLE;
