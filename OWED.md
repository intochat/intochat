# Owed

What a change introduced a new way for, without yet removing the old way. One line each: what is owed, and who owns it.
A pull request that adds a line here is not done until the line is gone.

- Rename `Circle*` types and stored identifiers to Friends and Groups, with a data migration for existing shared brains. Owner: the repo owner.
- Retire `CapabilityCaller` and `LegacyCallers`: the 81 os methods that take a caller as an argument read the unforgeable `Caller` instead (audit R07), authority areas pinned first and checked live. Owner: the repo owner.
