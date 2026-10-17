-- A null plugin scope covers all plugins the account owns now or in the future.
-- Existing tokens retain their specific plugin scope and its deletion behavior.
ALTER TABLE publisher_access_tokens ALTER COLUMN plugin_slug DROP NOT NULL;
