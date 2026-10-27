CREATE TABLE released_versions (
    plugin_slug       TEXT        NOT NULL,
    ver               INT[]       NOT NULL,
    build_id          BIGINT      NOT NULL,
    first_released_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (plugin_slug, ver)
);

INSERT INTO released_versions (plugin_slug, ver, build_id)
SELECT plugin_slug, ver, build_id FROM versions WHERE pre_release IS FALSE
ON CONFLICT DO NOTHING;

CREATE OR REPLACE FUNCTION record_released_version() RETURNS trigger AS $$
BEGIN
    IF NEW.pre_release IS FALSE THEN
        INSERT INTO released_versions (plugin_slug, ver, build_id)
        VALUES (NEW.plugin_slug, NEW.ver, NEW.build_id)
        ON CONFLICT DO NOTHING;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER versions_record_release
    AFTER INSERT OR UPDATE OF pre_release ON versions
    FOR EACH ROW EXECUTE FUNCTION record_released_version();

CREATE OR REPLACE FUNCTION protect_released_version() RETURNS trigger AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM released_versions r
               WHERE r.plugin_slug = NEW.plugin_slug AND r.ver = NEW.ver
                 AND (r.build_id <> NEW.build_id
                      OR (TG_OP = 'INSERT' AND NOT EXISTS (SELECT 1 FROM versions v
                                                           WHERE v.plugin_slug = NEW.plugin_slug AND v.ver = NEW.ver)))) THEN
        RAISE EXCEPTION 'Version % of % was already released and cannot point to another build',
            array_to_string(NEW.ver, '.'), NEW.plugin_slug
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER versions_protect_release
    BEFORE INSERT OR UPDATE OF build_id ON versions
    FOR EACH ROW EXECUTE FUNCTION protect_released_version();
