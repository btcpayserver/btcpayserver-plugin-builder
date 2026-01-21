-- Add created_at column to plugins table
ALTER TABLE plugins
ADD COLUMN added_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP;

-- Backfill created_at from the earliest build for each plugin
WITH earliest_builds AS (
    SELECT plugin_slug, MIN(created_at) AS earliest_created_at
    FROM builds
    GROUP BY plugin_slug
)
UPDATE plugins p
SET added_at = eb.earliest_created_at
FROM earliest_builds eb
WHERE p.slug = eb.plugin_slug;

