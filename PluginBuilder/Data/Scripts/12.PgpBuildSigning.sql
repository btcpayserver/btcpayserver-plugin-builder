ALTER TABLE builds
ADD COLUMN isbuildsigned BOOLEAN DEFAULT false;

UPDATE builds
SET isbuildsigned = true;
