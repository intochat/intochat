# Modules

A module is the unit of code that offers capabilities and declares what it needs.

```csharp
[Module("drive")]
public sealed class DriveModule : Module
{
    public static ModuleManifest Manifest { get; } = new(
        [new CapabilityId("storage", 1)],   // capabilities it offers
        [],                                 // capabilities it requires
        [Need.GrainStore("drive"), Need.Secrets],
        [],                                 // settings
        []);                                // secrets
}
```

## Needs

A `Need` names a kind of infrastructure, never a concrete service:

- `Need.GrainStore(name)`: a named grain store.
- `Need.Streams`, `Need.Reminders`, `Need.Secrets`, `Need.Http`, `Need.Sandbox`, `Need.Model`.

## How a topology binds them

At startup `UseTopology` reads `IntoChatOS:Topology`, selects its implementation and checks every module's needs
against it. Each topology binds every need kind: on `local` a grain store is a folder of files under
`<Home>/state/files/<name>`; on `cloud` it is Azure Blob storage. All issues are reported in one exception that names
the module and the configuration key, never a secret value.

The store names `platform` and `pubsubstore` are reserved. The `cluster` store is bound by the host.

See [Topology](topology.md).
