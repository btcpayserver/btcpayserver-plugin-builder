ALTER TABLE admin_api_audit ALTER COLUMN user_id DROP NOT NULL;
CREATE INDEX admin_events_created_idx ON admin_events(created_at);
CREATE INDEX admin_api_audit_started_idx ON admin_api_audit(started_at);
ALTER TABLE admin_event_deliveries DROP CONSTRAINT admin_event_deliveries_event_id_fkey;
ALTER TABLE admin_event_deliveries ADD FOREIGN KEY(event_id) REFERENCES admin_events(id) ON DELETE CASCADE;
