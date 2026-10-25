-- A trimmed copy of the front proxy's access log, for correlating abuse with accounts and builds. nginx sends it over
-- syslog; rows are kept for ACCESSLOG_RETENTION_DAYS (default 7) by logged_at, nginx's own clock. Query strings are
-- never stored: they can carry credentials.
CREATE TABLE nginx_access_log (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    logged_at timestamptz NOT NULL,
    ip inet NOT NULL,
    host text NOT NULL,
    method text NOT NULL,
    path text NOT NULL,
    status smallint NOT NULL,
    bytes bigint NOT NULL,
    user_agent text NOT NULL
);
CREATE INDEX nginx_access_log_logged_at_idx ON nginx_access_log(logged_at);
CREATE INDEX nginx_access_log_ip_idx ON nginx_access_log USING gist (ip inet_ops);
