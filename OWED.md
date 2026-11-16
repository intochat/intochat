# Owed

What a change introduced a new way for, without yet removing the old way. One line each: what is owed, and who owns it.
A pull request that adds a line here is not done until the line is gone.

- Rename `Circle*` types and stored identifiers to Friends and Groups, with a data migration for existing shared brains. Owner: the repo owner.
- Say "Groups" where screens still say "circle": the Settings team page, the Ino and Gallery samples, and the shell's own strings. Owner: the repo owner.
- Remove `CallFilter`, `ICallFilter` and `ConnectionCallBindings`, which the product never constructs (tests register them). Owner: the repo owner.
- Remove the `ShareableAttribute` declared in `src/Modules/Catalog/Contracts/Files/IVfsNeuron.cs`: nothing reads it, and inside its namespace it hides `DigitalBrain.Access.ShareableAttribute`. Owner: the repo owner.