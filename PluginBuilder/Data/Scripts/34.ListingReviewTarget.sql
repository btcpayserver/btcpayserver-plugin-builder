-- Existing requests have no historical snapshot; do not infer one from current builds.
ALTER TABLE plugin_listing_requests ADD COLUMN review_target jsonb;

CREATE OR REPLACE FUNCTION admin_listing_event() RETURNS trigger AS $$
BEGIN
    PERFORM emit_admin_event('listing.requested', jsonb_build_object('userId', NEW.submitted_by,
        'pluginSlug', NEW.plugin_slug, 'listingRequestId', NEW.id,
        'releaseNote', NEW.release_note, 'telegramVerificationMessage', NEW.telegram_verification_message,
        'userReviews', NEW.user_reviews, 'announcementDate', NEW.announcement_date) || COALESCE(NEW.review_target, '{}'::jsonb));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
