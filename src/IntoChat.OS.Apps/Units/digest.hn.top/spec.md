# Hacker News Scheduled Digest

Polls top Hacker News stories hourly, fetches the #1 ranked story title, URL, and score, and publishes a digest signal.

- Every `1h`, fetches top story IDs via `hn` slot and resolves the #1 story facts.
- Updates `TopTitle`, `TopScore`, and `TopUrl`.
- Publishes `hn.digested(int TopScore)`.
- The view renders the story card and score pill.
