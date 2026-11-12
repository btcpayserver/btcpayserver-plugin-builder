UPDATE plugins
SET settings = jsonb_set(settings, '{pluginTitle}', to_jsonb(btrim(settings->>'pluginTitle')))
WHERE jsonb_typeof(settings->'pluginTitle') = 'string';

ALTER TABLE plugins ADD CONSTRAINT plugins_title_length
    CHECK (char_length(settings->>'pluginTitle') <= 500);

CREATE UNIQUE INDEX plugins_title_unique
    ON plugins (lower(nullif(btrim(settings->>'pluginTitle'), '') COLLATE "pg_c_utf8"));
