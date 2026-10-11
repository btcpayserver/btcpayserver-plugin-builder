-- An admin lock, kept apart from Identity's lockout: failed logins also set "LockoutEnd", and only an admin lock
-- may suspend an account's builds. The lock endpoint sets both; unlock clears both.
CREATE TABLE admin_account_locks (
    user_id text PRIMARY KEY REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    locked_until timestamptz NOT NULL,
    reason text NOT NULL,
    locked_by text,
    locked_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
