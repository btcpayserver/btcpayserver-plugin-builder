ALTER TABLE versions
    ADD COLUMN btcpay_max_ver INT[] NULL,
    ADD COLUMN btcpay_max_ver_override_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN btcpay_min_ver_override_enabled BOOLEAN NOT NULL DEFAULT FALSE;

CREATE
OR REPLACE FUNCTION normalize_btcpay_version (version INT[])
RETURNS INT[]
AS $$
SELECT ARRAY[
    COALESCE(version[1], 0),
    COALESCE(version[2], 0),
    COALESCE(version[3], 0),
    COALESCE(version[4], 0)
]::INT[] $$ LANGUAGE SQL IMMUTABLE STRICT;

CREATE INDEX btcpay_max_ver_idx ON versions (normalize_btcpay_version(btcpay_max_ver));

CREATE
OR REPLACE FUNCTION get_latest_versions (btcpayVersion INT[], includePreRelease BOOLEAN)
RETURNS TABLE(plugin_slug TEXT, ver INT[], build_id BIGINT)
AS $$
WITH latest_versions AS
(
    SELECT plugin_slug, MAX(ver) ver FROM versions
    WHERE (
        btcpayVersion IS NULL OR (
            btcpay_min_ver <= normalize_btcpay_version(btcpayVersion)
            AND (
                btcpay_max_ver IS NULL
                OR normalize_btcpay_version(btcpayVersion) <= normalize_btcpay_version(btcpay_max_ver)
            )
        )
    ) AND (includePreRelease OR pre_release IS FALSE)
    GROUP BY plugin_slug
)
SELECT v.plugin_slug, v.ver, v.build_id
FROM latest_versions lv
         JOIN versions v USING (plugin_slug, ver) $$ LANGUAGE SQL STABLE;

CREATE
OR REPLACE FUNCTION get_all_versions (btcpayVersion INT[], includePreRelease BOOLEAN)
RETURNS TABLE(plugin_slug TEXT, ver INT[], build_id BIGINT)
AS $$
WITH latest_versions AS
(
    SELECT plugin_slug, ver FROM versions
    WHERE (
        btcpayVersion IS NULL OR (
            btcpay_min_ver <= normalize_btcpay_version(btcpayVersion)
            AND (
                btcpay_max_ver IS NULL
                OR normalize_btcpay_version(btcpayVersion) <= normalize_btcpay_version(btcpay_max_ver)
            )
        )
    ) AND (includePreRelease OR pre_release IS FALSE)
    ORDER BY plugin_slug, ver DESC
)
SELECT v.plugin_slug, v.ver, v.build_id
FROM latest_versions lv
         JOIN versions v USING (plugin_slug, ver) $$ LANGUAGE SQL STABLE;
