# Workspace Markdown Organizer

Periodically indexes markdown notes in the workspace, calculates note count, and detects recent notes.

- Indexes markdown notes and updates `NoteCount` and `LatestNote`.
- Publishes `notes.indexed(int NoteCount)`.
- Reacts to `notes.reset` by clearing the indexed notes.
- The view renders active note counts and latest note summary.
