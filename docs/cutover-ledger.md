# Cutover ledger

## Step 1 — one host (in progress)

### cutover(1): LegacySpi and NeedKind.State

Deleted: 1 file, 42 lines (`framework/src/DigitalBrain/Spi/LegacySpi.cs`).

Ported: timer and reminder ticks go to `Neuron.OnReminder` through `NeuronAttachment.Remind` (`HostTimers`, `NeuronHostGrainBase`, `BrainListenerNeuron`, `SessionSynapseNeuron`, `Fakes.ManualReminders`, `TestNeuronHost`, and the product neurons that overrode `IRemindedNeuron.OnReminder`). Deactivation was already `Neuron.OnDeactivate`. `INeuronTimers`, `IDeactivatingNeuron`, `IRuntimeEndpoint` and `ICallerContextCarrier` had no implementors.

`NeedKind.GrainStore` and `Need.GrainStore` are `NeedKind.State` and `Need.State`. The module template uses `Need.State("state")`. `GrainStoreBinding` stays in the Orleans runtime.

Tests: `DigitalBrain.Tests.Unit` 192 passed; `DigitalBrain.Testing.Tests.Unit` 315 passed, 17 skipped; `DigitalBrain.Runtime.Orleans.Tests.Unit` 224 passed; `VocabularyRingFacts` 6 passed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.

### cutover(1): legacy grain storage and retired reminders

Deleted: 9 files, 436 lines. The kernel `Storage` folder (legacy grain store, journal, persistent-state factory) and `RetiredReminders.cs`. The host registers BrainLog with `UseClusterStore(new BrainStorageRegistration(...))` and `OsBrainKeyCustody` before `ConfigureTopologySilo`, which is what calls `AddDigitalBrainRuntime`. Production custody is still checked by `BrainStoreCustody.Validate` on silo startup. `OsStorageFacts` and `RetiredReminderFacts` were DI and migration checks and were deleted with the code.

`IntoChat.OS.Kernel.Tests.Unit` 464 passed. `SyncOverAsyncFacts` 1 passed. `TopologyCompositionFacts` and `SideBySideRuntimeFacts` 5 passed. Solution build 0 errors, 0 warnings.

### cutover(1): one neuron host

Deleted: 12 files, 549 lines. `IPlainNeurons`, `IPlainSignals`, `IStillGrains`, `IBrainCalls`, `IAuthorityGrains`, the Authority grain facade, and the kernel `Calls` and `Authority` folders. `PlainNeuronCallFilter`, `PlainInstanceRegistration`, `RuntimeCallerMirror`, and `KernelInvocationSink` moved to `Kernel/` and stay registered. `AuthorityGrainsFacts` was a DI check and was deleted with the facade.

Ported: outside a neuron, `SiloNeurons` reaches the host grain through `OrleansInvocationSink`. A silo that loaded the class uses `NeuronTypeRegistry`; a client asks `INeuronHostCatalog` on `CompiledTypeTableGrain` (`/`). Streams are gated by `SiloNeurons.AuthorizeStreams` and `PlainNeuronCallFilter.Authorize`. Watches go through `SignalLog` and `ISessionSynapses`; history goes through `IBrainJournalWriter`. `IStillGrains` call sites are `IGrainFactory.GetGrain`. Model `Resolve`/`Complete` left the AI contracts assembly (`ModelNeurons` in the AI module, inlined on `CreatorModelWorker`).

`IntoChat.OS.Kernel.Tests.Unit` 464 passed. `StaticCacheFacts` and `VocabularyRingFacts` 2 passed. `NeuronHostFacts` 8 passed. `ProbeCallShapeFacts` and `InterleavingFacts` 24 passed. `ConfirmationRuntimeFacts` and `SideBySideRuntimeFacts` 14 passed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.

Deferred: `OrleansHostWiring` still applies the cloud container overlay and registers `IClusterMembershipFeed`. `CloudStorage` sets the module container and the keyed `grainstate` blob clients that cookie protection, deployment identity, and the C# module use. `ClusterNeuron` and `DeploymentNeuron` inject the membership feed. Removing either branch drops live callers. The cloud topology profile stays.

### Step 1 status

Deleted the plain-neuron, signal, still-grain, brain-call, and authority-grain facades (12 files, 549 lines) and pointed callers at `SiloNeurons`, `SignalLog`, and `IGrainFactory`. Four call-path types moved out of `Kernel/Calls` and stayed registered. The ambiguous `NeuronId` name under `IntoChat.OS` and a catalog lookup that fired inside a silo were the compile and host-test breaks. Unit and host facts above passed. `OrleansHostWiring`'s cloud storage overlay and membership feed stay because their callers are still live.
