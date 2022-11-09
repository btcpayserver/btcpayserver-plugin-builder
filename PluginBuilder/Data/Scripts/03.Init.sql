INSERT INTO "AspNetRoles" VALUES ('5ba004fa-2e7a-42a7-b310-c72e719b3c19', 'ServerAdmin', 'SERVERADMIN', '');

CREATE TABLE plugins
(
    slug TEXT NOT NULL PRIMARY KEY
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
    PRIMARY KEY (plugin_slug, id),
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE
);

CREATE TABLE versions
(
    plugin_slug TEXT NOT NULL,
    ver INT[] NOT NULL,
    build_id BIGINT NOT NULL,
    btcpay_min_ver INT[] NOT NULL,
    PRIMARY KEY (plugin_slug, ver),
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE,
    FOREIGN KEY (plugin_slug, build_id) REFERENCES builds (plugin_slug, id) ON DELETE CASCADE
);
