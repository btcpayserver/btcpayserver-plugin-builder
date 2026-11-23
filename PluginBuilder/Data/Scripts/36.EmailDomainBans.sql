CREATE TABLE email_domain_bans (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    domain varchar(253) NOT NULL UNIQUE CHECK (domain <> '' AND domain = lower(domain)),
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 1000 AND length(trim(reason)) > 0),
    banned_by text,
    banned_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
