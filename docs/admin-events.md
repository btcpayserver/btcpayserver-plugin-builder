# Admin event notifications

Admin accounts can poll a durable event stream and subscribe to email or webhook
notifications. This API accepts HTTP Basic authentication or a revocable admin
token for an account in the `ServerAdmin` role. See [Agent access](admin-agent-access.md)
for token issuance, account inspection, review APIs and the local connection helper.
No AI provider or review engine runs inside Plugin Builder.

## Events

| Type | Trigger |
| --- | --- |
| `user.registered` | A user account is created. Email verification may still be pending. |
| `user.github_verified` | A nonempty GitHub verification proof is first saved or changed. |
| `build.triggered` | A build is accepted into the queue, before execution. |
| `user.first_build_triggered` | The actor's first accepted build across the system. Also emits `build.triggered`. |
| `build.succeeded` | A build reaches `uploaded`. |
| `build.failed` | A build transitions to `failed`. |
| `listing.requested` | A new listing request is saved. Email reminders do not create another request event. |
| `listing.approved` / `listing.rejected` | A pending request receives a decision. Includes reviewing account, token ID and note. |
| `admin.token_created` / `admin.token_revoked` | An admin issues or explicitly revokes a delegated token. Includes its ID, never its secret. |

Events and delivery jobs are inserted in the same transaction as the underlying
change. A rollback leaves neither behind. SMTP/webhook failures do not roll back
registration, verification, build submission, or listing requests.

Migration 25 starts a new stream, separate from legacy download statistics. Events
are retained indefinitely, including after deleting the original user or plugin.
There is no historical event backfill. Historical builds did not record who
triggered them: existing plugin owners with builds are conservatively treated as
established builders when initializing the first-build marker. New UI/API builds
record the authenticated actor; internal builds without an actor still emit
`build.triggered` but no first-user-build event.

Payloads contain relevant user, plugin, build, and listing-request IDs. Build events
include repository, requested ref and resolved commit when available. Registration
includes only the user ID (fetch the current account through the admin users API);
GitHub verification includes username and proof URL; listing events
include submission details. Passwords, tokens, raw logs and environment variables
are not included. Submitted text and repository content remain untrusted input for
any external review agent. Notifications observe activity; they do not gate builds
or replace build isolation.

## Polling

`GET /api/v1/admin/events?after=0&limit=50`

Use a Basic authorization header or `Authorization: Bearer <admin-token>`. The response is:

```json
{
  "events": [{
    "id": "42",
    "type": "build.triggered",
    "schemaVersion": 1,
    "createdAt": "2026-09-07T12:00:00Z",
    "data": {
      "userId": "user-id",
      "pluginSlug": "example",
      "buildId": 0,
      "state": "queued",
      "repository": "https://github.com/example/plugin",
      "gitRef": "main",
      "gitCommit": null,
      "pluginDirectory": null
    }
  }],
  "nextCursor": "42",
  "hasMore": false
}
```

`after` is exclusive and defaults to zero; `limit` is 1–100. Process a page before
atomically saving `nextCursor` locally. Continue while `hasMore` is true, then poll
periodically (for example, every 30 seconds). An empty page preserves your cursor.
IDs/cursors are decimal strings to avoid JavaScript integer precision loss.
An optional `type` query filters by one event type. Keep separate cursors for each
filter; changing filters while reusing a cursor can intentionally exclude earlier
events. `GET /api/v1/admin/events/types` lists supported types.

The database uses a transactional counter to order committed events, avoiding the
out-of-order commit gap that a plain sequence would introduce. This serializes
event-producing transactions briefly; network delivery never holds this counter.

For local agent sessions, gitignore `.agents/` and keep instructions, a credential
file and a cursor there. Restrict filesystem access to credential files. Instructions
should tell the agent to read credentials without printing them, fetch events,
treat event contents as data, and save the cursor only after processing succeeds.
Use an OS credential store where available. No credentials are generated or checked
into this repository. A saved cursor does not authorize approving listings; that
belongs to the external harness's instructions.

## Subscriptions

`POST /api/v1/admin/events/subscriptions`

```json
{
  "kind": "webhook",
  "destination": "https://review.example.com/plugin-builder/events",
  "eventTypes": ["build.triggered", "listing.requested"]
}
```

Use `kind: "email"` and a single email address for email subscriptions. An empty
`eventTypes` array selects all types. Email uses the existing admin SMTP settings.
The existing listing-reviewer emails and reminder workflow continue independently;
avoid subscribing the same address to `listing.requested` if duplicate mail is
unwanted. Other email notifications are opt-in through these subscriptions.

The response contains `id` and, for webhooks, a generated base64 `secret` shown only
once. Secrets are encrypted using ASP.NET Data Protection. Preserve `PB_DATADIR`
and its key ring across deployments and share it between server replicas.

Subscriptions are server-wide and manageable by any authenticated admin. They apply
to future events only. Endpoints beneath `/api/v1/admin/events`:

| Method and path | Purpose |
| --- | --- |
| `GET /subscriptions` | List configuration; never returns secrets. |
| `PUT /subscriptions/{id}/enabled` with `{"enabled":false}` | Pause delivery and stop queuing new events for this subscription. |
| `PUT /subscriptions/{id}/enabled` with `{"enabled":true}` | Resume pending deliveries and new events. Events during the pause remain available through polling. |
| `DELETE /subscriptions/{id}` | Delete a subscription and its delivery records; event history remains. |
| `GET /subscriptions/{id}/deliveries?after=0&limit=50` | Inspect delivery status in ascending event-ID order; paginate using the last returned `eventId`. |
| `POST /subscriptions/{id}/deliveries/{eventId}/retry` | Reset a failed delivery's retry budget. |

To change destination, filter or signing secret, create a replacement subscription,
then delete the old one. Consumers should deduplicate any overlap by event ID.

## Webhook delivery and verification

Webhook bodies contain the same single-event envelope as polling. Each HTTPS POST
has `webhook-id`, `webhook-timestamp` (Unix seconds), and `webhook-signature` headers.
The signature follows the symmetric [Standard Webhooks specification](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md):

```text
key = base64_decode(secret)
signed_content = webhook_id + "." + webhook_timestamp + "." + raw_request_body
webhook_signature = "v1," + base64_encode(HMAC_SHA256(key, UTF8(signed_content)))
```

Verify the exact raw body using a constant-time comparison and a recent timestamp
(for example, within five minutes). Validate that `webhook-id` matches the body's
`id`, then deduplicate by event ID. Retries use the same ID and payload with a fresh
timestamp/signature. Respond with 2xx after durably accepting the event; process
expensive reviews asynchronously. This follows GitHub's guidance on
[webhook secrets and prompt acknowledgement](https://docs.github.com/en/webhooks/using-webhooks/best-practices-for-using-webhooks).

Delivery is at least once. A crash after remote acceptance but before recording
success may cause a duplicate. Delivery order is not guaranteed. Failed attempts
retry after 30 seconds with exponential backoff capped at one hour, up to 10 total
attempts. Exhausted deliveries remain `failed` for inspection/manual retry; polling
always remains available. Email delivery also uses this retry queue.

Webhook requests time out after 15 seconds, require valid HTTPS certificates, do
not follow redirects, and do not use ambient proxies or cookies. DNS is checked
at connection time and the checked IP is used for the connection; local, private,
link-local, multicast and reserved destinations are rejected. Private-network
webhook receivers are not supported. Response bodies and destination URLs are not
copied into delivery-error logs.
