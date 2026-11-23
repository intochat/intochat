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
| `Hosting` | `FlutterHostingOptions` | `FlutterHostingOptions { Kind = Window, ResourceName = , DeviceTarget = windows, ShellName = desk, ChatName = main, FlutterCommand = , WorkingDirectory = , ReleaseBuild = False, WebContainer = False }` | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Shell.Contracts` (`ICompute`, `IConversation`, `IField`, `IScreenDirectory`, `ISessionList`, `IUiRootProvider`, `IView`, `IViewport`, `IWebBrowser`, `IWorkspace`).
Hosting: runs in process.
<!-- END GENERATED -->
