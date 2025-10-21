CREATE TABLE IF NOT EXISTS plugin_reviews (
  id              BIGSERIAL PRIMARY KEY,
  plugin_slug     TEXT        NOT NULL,
  user_id         text        NOT NULL,
  rating          INT         NOT NULL CHECK (rating BETWEEN 1 AND 5),
  body            TEXT,
  plugin_version  INT[],
  helpful_voters  JSONB       NOT NULL DEFAULT '{}'::jsonb, -- {"<userId>": true|false}
  created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  UNIQUE (plugin_slug, user_id),

  CONSTRAINT fk_plugin_reviews_plugin
  FOREIGN KEY (plugin_slug)
  REFERENCES plugins (slug)
  ON DELETE CASCADE,

  CONSTRAINT fk_plugin_reviews_user
  FOREIGN KEY (user_id)
  REFERENCES "AspNetUsers" ("Id")
  ON DELETE CASCADE
);
