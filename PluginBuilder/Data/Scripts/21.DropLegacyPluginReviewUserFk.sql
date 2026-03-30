ALTER TABLE plugin_reviews
    DROP CONSTRAINT IF EXISTS fk_plugin_reviews_user;

ALTER TABLE plugin_reviews
    DROP COLUMN IF EXISTS user_id;
