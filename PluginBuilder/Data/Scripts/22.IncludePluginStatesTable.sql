CREATE TABLE IF NOT EXISTS plugin_downloads
(
    id  BIGSERIAL PRIMARY KEY,
    plugin_slug TEXT NOT NULL,
    version TEXT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    hashed_ip TEXT NOT NULL,
    btcpay_version TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_plugin_downloads_plugin_slug_timestamp ON plugin_downloads (plugin_slug, timestamp);


CREATE TABLE IF NOT EXISTS plugin_server_installs
(
    hashed_ip TEXT NOT NULL,
    plugin_slug TEXT NOT NULL,
    version TEXT NOT NULL,
    btcpay_version TEXT NOT NULL,
    install_count BIGINT NOT NULL DEFAULT 1,
    installed_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    uninstalled_at TIMESTAMPTZ NULL,
    PRIMARY KEY (hashed_ip, plugin_slug)
);

CREATE INDEX IF NOT EXISTS ix_plugin_server_installs_plugin_slug ON plugin_server_installs (plugin_slug);

CREATE INDEX IF NOT EXISTS ix_plugin_server_installs_plugin_slug_uninstalled ON plugin_server_installs (plugin_slug, uninstalled_at);
