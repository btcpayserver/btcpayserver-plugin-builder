ALTER TABLE versions ADD COLUMN download_stat BIGINT NOT NULL DEFAULT 0;

ALTER TABLE evts
ADD COLUMN plugin_slug TEXT,
ADD COLUMN build_id BIGINT,
ALTER COLUMN type TYPE VARCHAR(16),
ADD COLUMN id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
ADD COLUMN ip VARCHAR(45),
ADD CONSTRAINT unique_event UNIQUE (ip, plugin_slug, type);

CREATE INDEX idx_evts_plugin_slug ON evts(plugin_slug);
CREATE INDEX idx_evts_type ON evts(type);
CREATE INDEX idx_evts_build_id ON evts(build_id);

DROP FUNCTION IF EXISTS get_latest_versions(btcpayVersion INT[], includePreRelease BOOLEAN);
DROP FUNCTION IF EXISTS get_all_versions(btcpayVersion INT[], includePreRelease BOOLEAN);


CREATE OR REPLACE FUNCTION get_latest_versions (btcpayVersion INT[], includePreRelease BOOLEAN)
RETURNS TABLE(plugin_slug TEXT, ver INT[], build_id BIGINT, download_stat BIGINT)
AS $$
WITH latest_versions AS
(
    SELECT plugin_slug, MAX(ver) ver FROM versions
    WHERE (btcpayVersion IS NULL OR btcpay_min_ver <= btcpayVersion) AND (includePreRelease OR pre_release IS FALSE)
    GROUP BY plugin_slug
)
SELECT v.plugin_slug, v.ver, v.build_id, v.download_stat FROM latest_versions lv
JOIN versions v USING (plugin_slug, ver)

$$ LANGUAGE SQL STABLE;


CREATE OR REPLACE FUNCTION get_all_versions (btcpayVersion INT[], includePreRelease BOOLEAN)
RETURNS TABLE(plugin_slug TEXT, ver INT[], build_id BIGINT, download_stat BIGINT)
AS $$
WITH latest_versions AS
(
    SELECT plugin_slug, ver FROM versions
    WHERE (btcpayVersion IS NULL OR btcpay_min_ver <= btcpayVersion) AND (includePreRelease OR pre_release IS FALSE)
    ORDER BY plugin_slug, ver DESC
)
SELECT v.plugin_slug, v.ver, v.build_id, v.download_stat
FROM latest_versions lv
         JOIN versions v USING (plugin_slug, ver) $$ LANGUAGE SQL STABLE;

