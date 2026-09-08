CREATE TABLE build_whitelist
(
    user_id text PRIMARY KEY REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
);
