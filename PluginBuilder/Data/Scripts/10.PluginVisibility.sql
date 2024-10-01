CREATE TYPE plugin_visibility_enum AS ENUM ('hidden', 'unlisted', 'listed');
ALTER TABLE public.plugins
ADD COLUMN visibility plugin_visibility_enum NOT NULL DEFAULT 'unlisted';
