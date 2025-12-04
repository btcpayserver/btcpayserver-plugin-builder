CREATE TABLE IF NOT EXISTS plugin_reviewers (
  id BIGSERIAL PRIMARY KEY,
  user_id TEXT UNIQUE,
  username TEXT,
  source TEXT,
  profile_url TEXT,
  avatar_url TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_plugin_reviewers_source_username ON plugin_reviewers (source, username);

INSERT INTO plugin_reviewers (user_id, source, created_at, updated_at)
SELECT DISTINCT user_id, 'system', NOW(), NOW()
FROM   plugin_reviews
WHERE  user_id IS NOT NULL
ON CONFLICT (user_id) DO NOTHING;


ALTER TABLE plugin_reviews
ALTER COLUMN user_id DROP NOT NULL,
ADD COLUMN IF NOT EXISTS reviewer_id BIGINT;


UPDATE plugin_reviews r SET reviewer_id = p.id FROM plugin_reviewers p WHERE p.user_id = r.user_id;

-- Critical: Verify all reviews have a reviewer_id before setting NOT NULL constraint
-- This prevents migration failure if there are orphaned reviews
DO $$
DECLARE
    orphaned_count INTEGER;
BEGIN
    SELECT COUNT(*) INTO orphaned_count FROM plugin_reviews WHERE reviewer_id IS NULL;
    
    IF orphaned_count > 0 THEN
        RAISE NOTICE 'Found % orphaned reviews without reviewer_id. Deleting them to proceed with migration.', orphaned_count;
        DELETE FROM plugin_reviews WHERE reviewer_id IS NULL;
    END IF;
END $$;

ALTER TABLE plugin_reviews ALTER COLUMN reviewer_id SET NOT NULL;

ALTER TABLE plugin_reviews
  DROP CONSTRAINT IF EXISTS plugin_reviews_plugin_slug_user_id_key,
  ADD CONSTRAINT plugin_reviews_plugin_slug_reviewer_id_key UNIQUE (plugin_slug, reviewer_id),
  ADD CONSTRAINT fk_plugin_reviews_reviewer
        FOREIGN KEY (reviewer_id) REFERENCES plugin_reviewers(id)
        ON DELETE CASCADE;
