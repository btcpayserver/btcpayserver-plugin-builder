-- Track signup time for stale-account cleanup and speed lookups by confirmation state.
ALTER TABLE "AspNetUsers"
    ADD COLUMN IF NOT EXISTS "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP;

CREATE INDEX IF NOT EXISTS "IX_AspNetUsers_EmailConfirmed_CreatedAt"
    ON "AspNetUsers" ("EmailConfirmed", "CreatedAt");
