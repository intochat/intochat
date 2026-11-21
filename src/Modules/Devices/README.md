# IntoChat.OS.Modules.Devices

This machine: hardware, local model runtimes and model installs.

Module id: `devices`.

How to test: `dotnet test --project tests/IntoChat.OS.Tests.Unit/IntoChat.OS.Tests.Unit.csproj -- --filter-namespace "*Modules.Devices*"`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:devices:Options` (environment prefix `IntoChatOS__Modules__devices__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Devices.Contracts` (`IDeviceInstallReports`, `IDevice`, `ILocalRuntime`, `IVoiceActivityDetector`, `IVoiceActivity`).
Hosting: runs in process.
<!-- END GENERATED -->
