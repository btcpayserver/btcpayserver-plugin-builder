-- Signup evidence expires after seven days; explicit bans remain until revoked.
ALTER TABLE "AspNetUsers" ADD COLUMN "SignupIp" inet
    CHECK ("SignupIp" IS NULL OR masklen("SignupIp") = CASE family("SignupIp") WHEN 4 THEN 32 ELSE 128 END);

CREATE TABLE ip_bans (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    network cidr NOT NULL UNIQUE,
    reason text NOT NULL,
    banned_by text,
    banned_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (masklen(network) = CASE family(network) WHEN 4 THEN 32 ELSE 128 END
           OR (family(network) = 6 AND masklen(network) = 64))
);
