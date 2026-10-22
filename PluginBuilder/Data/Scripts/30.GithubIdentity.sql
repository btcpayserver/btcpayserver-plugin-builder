-- The login is display metadata; only the numeric owner ID from a verified gist identifies a GitHub account.
-- Existing proofs remain valid and their IDs are recovered when their owners refresh verification.
ALTER TABLE "AspNetUsers" ADD COLUMN "GithubUserId" bigint CHECK ("GithubUserId" > 0);
CREATE INDEX aspnetusers_github_user_id_idx ON "AspNetUsers" ("GithubUserId") WHERE "GithubUserId" IS NOT NULL;
