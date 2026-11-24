# IntoChat.OS.Modules.Circles

Legacy read-only circles (kept readable for two releases after S8). Friends, groups and device enrollment live in `Catalog` (`IFriends`), `AI`/`Shell` (`IConversation`) and `Devices` (`IDeviceEnrollment`).

Module id: `circles`.

How to test: `dotnet test --project tests/IntoChat.OS.Tests.Unit -- --filter-class "*LegacyCirclesFacts"`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:circles:Options` (environment prefix `IntoChatOS__Modules__circles__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Circles` (`ILegacyCircleChat`, `ILegacyCircleIndex`, `ILegacyCircleMachines`, `ILegacyCircle`).
Hosting: runs in process.
<!-- END GENERATED -->
