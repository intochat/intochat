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

Deleted: 11 files, 522 lines. `IPlainNeurons`, `IPlainSignals`, `IStillGrains`, `IBrainCalls`, `IAuthorityGrains`, the Authority grain facade, and the kernel plain-neuron and plain-signal implementations. `PlainNeuronCallFilter`, `PlainInstanceRegistration`, `RuntimeCallerMirror`, and `KernelInvocationSink` moved to `Kernel/` and stay registered. `BrainCalls` moved to `BrainActs` in Circles. `AuthorityGrainsFacts` was a DI check and was deleted with the facade.

Ported: outside a neuron, `SiloNeurons` reaches the host grain through `OrleansInvocationSink`. A silo that loaded the class uses `NeuronTypeRegistry`; a client asks `INeuronHostCatalog` on `CompiledTypeTableGrain` (`/`). Streams are gated by `SiloNeurons.AuthorizeStreams` and `PlainNeuronCallFilter.Authorize`. Watches go through `SignalLog` and `ISessionSynapses`; history goes through `IBrainJournalWriter`. `IStillGrains` call sites are `IGrainFactory.GetGrain`. Model `Resolve`/`Complete` left the AI contracts assembly (`ModelNeurons` in the AI module, inlined on `CreatorModelWorker`).

`IntoChat.OS.Kernel.Tests.Unit` 464 passed. `StaticCacheFacts` and `VocabularyRingFacts` 2 passed. `NeuronHostFacts` 8 passed. `ProbeCallShapeFacts` and `InterleavingFacts` 24 passed. `ConfirmationRuntimeFacts` and `SideBySideRuntimeFacts` 14 passed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.

Deferred: `OrleansHostWiring` still applies the cloud container overlay and registers `IClusterMembershipFeed`. `CloudStorage` sets the module container and the keyed `grainstate` blob clients that cookie protection, deployment identity, and the C# module use. `ClusterNeuron` and `DeploymentNeuron` inject the membership feed. Removing either branch drops live callers. The cloud topology profile stays.

### Step 1 status

Deleted the plain-neuron, signal, still-grain, brain-call, and authority-grain facades (11 files, 522 lines) and pointed callers at `SiloNeurons`, `SignalLog`, and `IGrainFactory`. Four call-path types moved out of `Kernel/Calls` and stayed registered. The ambiguous `NeuronId` name under `IntoChat.OS` and a catalog lookup that fired inside a silo were the compile and host-test breaks. Unit and host facts above passed. `OrleansHostWiring`'s cloud storage overlay and membership feed stay because their callers are still live.

## Step 2 — one vocabulary (blocked)

### cutover(2): drop Orleans serializers from Contracts projects (`c4b5460d0`)

Stripped `[GenerateSerializer]`, `[Alias]`, `[Immutable]`, and `[Id]` from `*.Contracts` projects. Dropped `Microsoft.Orleans*` package references from those projects. Payloads stay JSON. `CallerContext` stays in the host; identity collapse is step 4.

### cutover(2): host a module as either base through OsModule (`d48251506`)

`OsModule` became the host descriptor for a module. Catalog, installer, and server entry points take `OsModule`.

### cutover(2): host product modules as DigitalBrain modules (`c69ac9c92`)

Every product module under `src/IntoChat.OS.Modules` and the cloud Auth and Circle modules is `[Module(id)]` plus a static `ModuleManifest`. The old `Module<TOptions>` subclasses are gone. `ConfigureServices` stays a static method the host calls. Time's reminder period is bound by `OrleansHostWiring`. Auth and Circle load only when `Topology=cloud`, and the kernel project references them.

### cutover(2): move the host out of src/IntoChat.OS.Framework (`092e7e5a2`)

Deleted the generator, the generator tests, and the Framework unit, integration, and E2E test projects. Moved the host and the zero-dependency `Contracts.Compute` and `Contracts.Configuration` projects under `src/IntoChat.OS`. `src/IntoChat.OS.Framework` is gone. The parent `IntoChat.OS` project excludes those trees so they are not compiled twice. Identity types stay in the moved host for step 4.

### cutover(2): drop the Framework module base and the catalog scan (`1e24de375`)

Deleted `Module`, `Module<TOptions>`, and `ModuleOptionsBinding` (76 lines). Deleted `OsCatalog.cs` and `OsCatalog.Generated.cs`. `OperatingSystemModules` lists `[Module]` types from assemblies the kernel project references (`DependencyContext` plus a reference walk), including `IntoChat.Cloud.Auth` and `IntoChat.Cloud.Circle.Server`. No folder glob. HTTP modules register as `IHttpModule`.

`dotnet build Intochat.OS.slnx -c Release` before this commit: 0 errors, 0 warnings.

Tests: `OsCatalogFacts` and `ProductCompositionFacts` 13 passed. Azure `ModuleDeploymentFacts` and `ModuleSecretFacts` 14 passed. Aspire module-hosting facts 20 passed. `CSharpFacts` 19 passed. `BoundaryFacts` passed inside an architecture filter that also ran `RuntimeBoundaryFacts` (44 passed, 2 failed, 2 skipped). The two failures are `GrainFactoryUseOnlyShrink` and `OrleansPackageReferencesOnlyShrink`. Their JSON baselines were not rewritten. Step 6 deletes those ratchets.

### Step 2 status

Product modules are DigitalBrain modules, the Framework folder and the old module base are gone, Contracts projects do not reference Orleans, and `OsCatalog` is gone. The solution builds with 0 warnings. The old neuron, signal, synapse, and kernel vocabulary is still in `src/IntoChat.OS`, and module projects that still call Orleans still reference it. `OrleansHostWiring`'s cloud storage overlay and `IClusterMembershipFeed` stay; their callers are still live.

## BLOCKED

Deleting `src/IntoChat.OS/{Neurons,Signals,Synapses,Kernel}` and `Contracts/NeutralAttributes.cs` needs an owner mapping this plan does not give. `DigitalBrain.NeuronId` is `brain/type/key`. `IntoChat.OS.NeuronId` is a string name, and `IIntoChatOS.Get` takes it. `DigitalBrain.Signal` has no publisher or trace; `IntoChat.OS.Signal` stamps both. `IIntoChatOS` (74 files), `IKernelView` (29), and `RevisionConflictException` (39) have no DigitalBrain type to move onto. Moving the old types to another folder would keep the second vocabulary. `Runtime/` stays, as step 2's "delete the project if empty" rule does not apply while these types remain.

`IntoChatOSNames` topology strings stay. They are the configuration spelling for Orleans-free projects (ServiceDefaults, AppHost, Mcp). `Topology` lives in `DigitalBrain.Runtime.Orleans`. Referencing that assembly from `Contracts.Configuration` would put Orleans on the zero-dependency configuration contract. `TopologyKeysMatchRuntimeFacts` already pins the strings to `Topology.ToConfigValue()`.

Module projects that still call Orleans keep their `Microsoft.Orleans*` package references: AI, Flutter, CSharp, Circle.Server, and the host. Time's reminder binding already moved to `OrleansHostWiring`. The plan does not say where the remaining grain-factory, reminder, and `RequestContext` calls go, so those references stay until that destination is named.
