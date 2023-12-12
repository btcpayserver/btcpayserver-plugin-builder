CREATE OR REPLACE FUNCTION get_all_versions (btcpayVersion INT[], includePreRelease BOOLEAN)
RETURNS TABLE(plugin_slug TEXT, ver INT[], build_id BIGINT)
AS $$
WITH latest_versions AS
(
    SELECT plugin_slug, ver FROM versions
    WHERE (btcpayVersion IS NULL OR btcpay_min_ver <= btcpayVersion) AND (includePreRelease OR pre_release IS FALSE)
    ORDER BY plugin_slug, ver DESC
)
SELECT v.plugin_slug, v.ver, v.build_id
FROM latest_versions lv
         JOIN versions v USING (plugin_slug, ver) $$ LANGUAGE SQL STABLE;
