-- The login is display metadata; only the numeric owner ID from a verified gist identifies a GitHub account.
-- Existing proofs remain valid with a null numeric ID.
ALTER TABLE "AspNetUsers" ADD COLUMN "GithubUserId" bigint CHECK ("GithubUserId" > 0);
