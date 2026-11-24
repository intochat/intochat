# IntoChat.OS.Modules.Devices

This machine: hardware, local model runtimes and model installs.

Device enrollment belongs to the brain that mints the code (`IDeviceEnrollment`). The cloud relay runs in its own cluster and asks the enrollment edge (`EnrollmentEdge`: redeem, list, live) with a signed service assertion from a trusted issuer listed with `Services`; the edge then acts as this module in the brain the code or route names. Device chat goes the other way: the brain's install asks the cloud relay over HTTP (`DeviceRoutes`), signed with the machine credential its device link holds, for its brain's other online devices and streams a chat through one of them (`IDeviceChatRoute`).

Module id: `devices`.

How to test: `dotnet test --project tests/IntoChat.OS.Tests.Unit/IntoChat.OS.Tests.Unit.csproj -- --filter-namespace "*Modules.Devices*"`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:devices:Options` (environment prefix `IntoChatOS__Modules__devices__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Devices.Contracts` (`IDeviceChatRoute`, `IDeviceEnrollmentRedeemer`, `IDeviceEnrollment`, `IDeviceInstallReports`, `IDeviceLink`, `IDevice`, `ILocalRuntime`, `INonceCache`, `IVoiceActivityDetector`, `IVoiceActivity`).
Hosting: runs in process.
<!-- END GENERATED -->
