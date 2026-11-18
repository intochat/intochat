# Owed

What a change introduced a new way for, without yet removing the old way. One line each: what is owed, and who owns it.
A pull request that adds a line here is not done until the line is gone.

- Rename `Circle*` types and stored identifiers to Friends and Groups, with a data migration for existing shared brains. Owner: the repo owner.
- `ConnectionStorage.Typed` (src/IntoChat.OS.Kernel) lifts an `Actor.App` caller to the kernel of its brain for every provider call, carried from the old Platform stamp (caller-model collapse, Task 6). Narrow it to an explicit connector module actor, or to the app itself once providers admit it. Owner: the repo owner.
