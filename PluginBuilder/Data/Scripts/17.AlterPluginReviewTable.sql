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

CREATE UNIQUE INDEX IF NOT EXISTS idx_plugin_reviewers_user_id ON plugin_reviewers (user_id);
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


ALTER TABLE plugin_reviews ALTER COLUMN reviewer_id SET NOT NULL;

ALTER TABLE plugin_reviews
  DROP CONSTRAINT IF EXISTS plugin_reviews_plugin_slug_user_id_key,
  ADD CONSTRAINT plugin_reviews_plugin_slug_reviewer_id_key UNIQUE (plugin_slug, reviewer_id),
  ADD CONSTRAINT fk_plugin_reviews_reviewer
        FOREIGN KEY (reviewer_id) REFERENCES plugin_reviewers(id)
        ON DELETE CASCADE;
