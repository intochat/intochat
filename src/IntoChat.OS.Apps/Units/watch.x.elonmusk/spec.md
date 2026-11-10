# Musk X Watch

Watches mentions of @elonmusk on X every 6 minutes, counts the mentions it has not counted before, updates the latest mention text, and maintains an unseen count until marked seen.

- Every `6m`, reads mentions through the `x` slot, counts the mentions newer than the newest one already counted (`LastId`), adds that to `Unseen`, updates `Latest` and `LastId`, and emits `x.polled`.
- Reacts to `x.seen` signal by resetting `Unseen` to 0.
- The view renders an unseen badge (`red` tone when > 10, `accent` otherwise), latest mention text, and a "Mark seen" button.
