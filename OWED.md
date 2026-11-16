# Owed

What a change introduced a new way for, without yet removing the old way. One line each: what is owed, and who owns it.
A pull request that adds a line here is not done until the line is gone.

- Rename `Circle*` types and stored identifiers to Friends and Groups, with a data migration for existing shared brains. Owner: the repo owner.
- Say "Groups" where screens still say "circle": the Settings team page, the Ino and Gallery samples, and the shell's own strings. Owner: the repo owner.
- Decide what becomes of `ICallBindings` and `ConnectionCallBindings`. The product registers them, but their only caller was the removed `CallFilter`; two unit test files still exercise them directly. Owner: the repo owner.
- Give the in-memory test runtime (TestOS) the same rule as the silo for calls between brains: only a Shared or Public operation of a `[Shareable]` contract crosses. Today TestOS lets any Shared or Public operation cross. Owner: the repo owner.
