# Owed

What a change introduced a new way for, without yet removing the old way. One line each: what is owed, and who owns it.
A pull request that adds a line here is not done until the line is gone.

- Rename `Circle*` types and stored identifiers to Friends and Groups, with a data migration for existing shared brains. Owner: the repo owner.
- Retire `CapabilityCaller` and `LegacyCallers` (audit R07). Done for the five wire contracts (`IVault`, `IOsVault`, `IDirectoryStore`/`IIdentityClient` owner grants, `IBrainEntries.Release`, `IIntegration.Release`). Still owed: the in-process ports, `GrantRules`, `BrainAuthorityDecisions`, `EdgeGrantsCache`, the modules and MCP; the framework `IDirectory` (Identity.Local), whose six methods still take a caller; and production `AccessOptions.AuditContractInternal`, which still admits an unstamped call to a non-public contract as the kernel. Owner: the repo owner.
- `ConnectionStorage.Typed` (src/IntoChat.OS.Kernel) lifts an `Actor.App` caller to the kernel of its brain for every provider call, carried from the old Platform stamp (caller-model collapse, Task 6). Narrow it to an explicit connector module actor, or to the app itself once providers admit it. Owner: the repo owner.
