-- Add created_at column to plugins table
ALTER TABLE plugins
ADD COLUMN added_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP;

-- Backfill added_at from the earliest build for each plugin
WITH earliest_builds AS (
    SELECT plugin_slug, MIN(created_at) AS earliest_created_at
    FROM builds
    GROUP BY plugin_slug
)
UPDATE plugins p
SET added_at = eb.earliest_created_at
FROM earliest_builds eb
WHERE p.slug = eb.plugin_slug;

-- Legacy plugins with no builds get backfilled to 5 months ago so they expire sooner
UPDATE plugins p
SET added_at = NOW() - INTERVAL '5 months'
WHERE NOT EXISTS (
    SELECT 1 FROM builds b WHERE b.plugin_slug = p.slug
);
