CREATE TABLE migrations (
  script_name TEXT NOT NULL PRIMARY KEY,
  executed_at timestamptz DEFAULT CURRENT_TIMESTAMP);
