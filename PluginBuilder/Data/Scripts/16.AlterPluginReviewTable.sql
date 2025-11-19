ALTER TABLE plugin_reviews
ALTER COLUMN user_id DROP NOT NULL,
ADD COLUMN author_username TEXT,
ADD COLUMN author_profile_url TEXT,
ADD COLUMN author_avatar_url TEXT;
