# Daily brief

Maintains a daily focus brief of open priorities and completed items.

- `brief.add {"text": "..."}` (or `{"value": "..."}`) appends a non-empty priority once to `Priorities`.
- `brief.complete {"text": "..."}` (or `{"value": "..."}`) moves a matching item from `Priorities` into `Completed`.
- `brief.reset` clears `Completed` while keeping open `Priorities`.
- The view renders a heading, an Open vs Completed stat card, a column of priority cards, and a `priority` text field that emits `brief.add`.
