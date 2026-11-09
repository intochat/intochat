# Webhook relay

Captures incoming webhook events by topic and summary, keeping the latest 100 events.

- `relay.ingest {"topic": "...", "summary": "..."}` appends a `RelayEvent` to `Events` (capped at 100).
- `relay.clear` clears all captured `Events`.
- The view displays a heading, a badge with the captured event count, a column of cards for each event, and a `clear` button that emits `relay.clear`.
