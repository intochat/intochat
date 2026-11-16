# IntoChat.OS.Modules.Flutter

Flutter module integration for the neuron kernel.

Module id: `flutter`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Google/Flutter/tests/IntoChat.OS.Modules.Flutter.Tests.Hosting/IntoChat.OS.Modules.Flutter.Tests.Hosting.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:flutter:Options` (environment prefix `IntoChatOS__Modules__flutter__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `Hosting` | `FlutterHostingOptions` | `FlutterHostingOptions { Kind = Window, ResourceName = FlutterShell, DeviceTarget = windows, ShellName = desk, ChatName = main, FlutterCommand = , WorkingDirectory = , ReleaseBuild = False, WebContainer = False }` | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Framework` (`IBrainBirths`, `IBrainEntries`, `IBrainMembership`, `IBrainOwnershipCounters`, `IBrainProviderKeys`, `ICallFilterStage`, `IChatIndex`, `IChat`, `IClusterMembershipFeed`, `ICluster`, `ICompute`, `IConfinedProcessRunner`, `IConnectionBindings`, `IContactsNeuron`, `ICreationHandler`, `IEdgeGrantsCache`, `IField`, `IGrantsNeuron`, `IHandleDirectory`, `IHostedAppTypes`, `IIdentityClient`, `IIdentityDirectory`, `IIntegration`, `IIntoChatOSBrains`, `IKernel`, `IKeyWrapper`, `ILicenseCheck`, `IMachineIdentity`, `IMachineKeyStore`, `IModuleTypeSource`, `IPresenceProof`, `IRevisioned`1`, `IScreenDirectory`, `ISecretRedactor`, `ISecretValueSource`, `ITopologyModule`, `IView`, `IViewport`, `IWebBrowser`, `IWorkspace`).
Hosting: runs in process.
<!-- END GENERATED -->
