CREATE TABLE evts
(
    type       TEXT        NOT NULL,
    data       JSONB       NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX evts_created_at_idx ON evts (created_at);
