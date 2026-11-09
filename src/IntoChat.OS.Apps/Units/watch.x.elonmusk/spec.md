# Musk X Watch

Watches mentions of @elonmusk on X every 6 minutes, counts fresh mentions, updates the latest mention text, and maintains an unseen count until marked seen.

- Every `6m`, reads mentions through the `x` slot, increments `Unseen` by the fresh count, updates `Latest`, and emits `x.polled`.
- Reacts to `x.seen` signal by resetting `Unseen` to 0.
- The view renders an unseen badge (`red` tone when > 10, `accent` otherwise), latest mention text, and a "Mark seen" button.
