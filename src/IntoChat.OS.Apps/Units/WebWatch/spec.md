# Web watch

Polls an HTTP JSON feed every six minutes, counts new items, and tracks unseen updates until marked seen.

- Every `6m`, reads `items` from the `http` slot, increments `Unseen` by the item count, updates `Latest` from the first item's `title`, and publishes `watch.polled`.
- `watch.seen` resets `Unseen` to `0`.
- The view displays a heading with the latest item title, an unseen badge (`error` tone above 10, `accent` otherwise), and a `mark-seen` button emitting `watch.seen`.
