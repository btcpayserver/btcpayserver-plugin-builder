-- Create plugin listing requests table with proper workflow tracking
CREATE TABLE plugin_listing_requests
(
    id                           SERIAL PRIMARY KEY,
    plugin_slug                  TEXT        NOT NULL,
    release_note                 TEXT        NOT NULL,
    telegram_verification_message TEXT       NOT NULL,
    user_reviews                 TEXT        NOT NULL,
    announcement_date            TIMESTAMPTZ,
    status                       TEXT        NOT NULL DEFAULT 'pending', -- pending, approved, rejected, resubmitted
    submitted_at                 TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    reviewed_at                  TIMESTAMPTZ,
    reviewed_by                  TEXT, -- admin user id
    rejection_reason             TEXT,
    FOREIGN KEY (plugin_slug) REFERENCES plugins (slug) ON DELETE CASCADE,
    FOREIGN KEY (reviewed_by) REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL
);

CREATE INDEX idx_plugin_listing_requests_plugin_slug ON plugin_listing_requests (plugin_slug);
CREATE INDEX idx_plugin_listing_requests_status ON plugin_listing_requests (status);
CREATE INDEX idx_plugin_listing_requests_submitted_at ON plugin_listing_requests (submitted_at DESC);

-- Migrate existing listing requests from JSON to table
INSERT INTO plugin_listing_requests (plugin_slug, release_note, telegram_verification_message, user_reviews, announcement_date, submitted_at, status)
SELECT 
    slug,
    settings->'requestListing'->>'releaseNote',
    settings->'requestListing'->>'telegramVerificationMessage',
    settings->'requestListing'->>'userReviews',
    CASE 
        WHEN settings->'requestListing'->>'announcementDate' IS NOT NULL 
        THEN (settings->'requestListing'->>'announcementDate')::timestamptz 
        ELSE NULL 
    END,
    COALESCE(
        CASE 
            WHEN settings->'requestListing'->>'dateAdded' IS NOT NULL 
            THEN (settings->'requestListing'->>'dateAdded')::timestamptz 
            ELSE NULL 
        END,
        CURRENT_TIMESTAMP
    ),
    'pending'
FROM plugins
WHERE settings->'requestListing' IS NOT NULL
  AND settings->'requestListing'->>'releaseNote' IS NOT NULL;
