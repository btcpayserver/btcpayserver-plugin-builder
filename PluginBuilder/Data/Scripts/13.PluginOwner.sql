ALTER TABLE users_plugins
ADD COLUMN is_primary_owner BOOLEAN DEFAULT FALSE;

CREATE UNIQUE INDEX ux_one_primary_owner_per_plugin
ON users_plugins(plugin_slug)
WHERE is_primary_owner = TRUE;
