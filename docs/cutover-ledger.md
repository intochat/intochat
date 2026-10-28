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

## Step 2 — one vocabulary

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

### Step 2 — owner mapping

Owner mapping for the old `src/IntoChat.OS` vocabulary, following DESIGN-digitalbrain.md §1 (approved 2026-10-24).

1. `IIntoChatOS.Get<T>(NeuronId)` is gone with `IIntoChatOS`. Inside a neuron, `Neuron.Get<T>(NeuronId)`. Outside a neuron, `DigitalBrain.Spi.INeuronProxies.Get<T>`. A string os key uses `IntoChat.OS.Runtime.NeuronProxies.Get<T>(this INeuronProxies, string)`. The script edge keeps `IntoChatOSConnection.Get<T>(string)` and `Neuron<T>(string)`. `IIntoChatOSBrains.Get<T>(string)` and `On<T>` stay. `Open` returns `ISignalWatch`.
2. `IIntoChatOS.On<T>` await-foreach is an `[On]` reaction plus `Context.Synapses.Ensure<T>` inside a neuron. Outside a neuron, the session synapse is `SessionSynapseNeuron`. A host observer that is neither takes `IClusterListeners` from `Spi.cs`. `ISignalWatch` is the watch surface (`On<T>` only). Its two-argument `On<T>(object, CancellationToken)` forwards to the three-argument form.
3. `IntoChat.OS.NeuronId` is `DigitalBrain.NeuronId(Brain, Type, Key)`. `Parse` and `TryParse` were already on `framework/src/DigitalBrain/Identity/NeuronId.cs`. There is no implicit string conversion and no `NeuronIdOs.FromLegacy`. A full os key or a relative name such as `apps/assistant` goes through `PlainNeuronContracts.IdOf` or `SiloNeurons.IdOf`. The type comes from `[DefaultNeuronType]` (or the connection connector). Splitting `acc/apps/settings` so the type is `apps` addresses the wrong neuron. `TraceContext.OriginOf(brain, osKey)` addresses the cost-trace origin: `{brain}/edge` becomes type `edge`, key `request`, so `Origin.ToString()` is `{brain}/edge/request`. `IBrainOwnerDispatch.RunAsync` takes that origin as a string.
4. `IntoChat.OS.Signal` is `DigitalBrain.Signal`, which has no fields and is abstract. `signal.Publisher` is the host journal `IntoChat.OS.Framework.JournaledSignal.Publisher` (a string, `GrainId.Key`) or the reaction delivery context. `signal.Trace` is `System.Diagnostics.Activity.Current`. The reaction context did not gain a `Publisher`. The compute-cost record `IntoChat.OS.Trace` (alias `brain.trace`) is a different type and stays in `src/IntoChat.OS/Runtime/Trace.cs` with `Origin` as `DigitalBrain.NeuronId`. `SignalWireFacts` only proved the old `Publisher`/`Trace` wire and was deleted.
5. `IKernelView` moved to `DigitalBrain.Runtime.Orleans.IBrainObserver` (`Instances`, `Types`, `Brains`). `Running` and `InstancesIn` stay on product `IKernel`, which extends `IBrainObserver`. `AddDigitalBrainRuntime` registers `ClusterBrainObserver`. `AddIntoChatOS` replaces that registration with product `Kernel`. `SessionKernel` is the script-visible kernel: a public class, internal constructor, not typed as `IBrainObserver`. The wire name `RuntimeProtocol.KernelContract` is still `"IntoChat.OS.IKernelView"`.
6. `RevisionConflictException`, `RollbackDetectedException`, and `JournalConflictException` are `DigitalBrain.RevisionConflictException`. The message is preserved. `JOURNAL_DIVERGED` is no longer a separate status. `ServiceUnavailableException` and `StaleActionException` moved to `src/IntoChat.OS/Runtime/` and keep the namespace `IntoChat.OS`.
7. `Contracts/NeutralAttributes.cs` is deleted. Scheduling attributes are `framework/src/DigitalBrain/Attributes/SchedulingAttributes.cs`.
8. `IntoChatOSNames` stays in both `IntoChat.OS.Contracts.Configuration` and `DigitalBrain.Runtime.Orleans`. `TopologyKeysMatchRuntimeFacts` pins them. A file that imports both namespaces aliases the product type. Not a blocker.
9. Orleans package references that remain because the caller still uses the grain factory, reminders, or `RequestContext` are listed under "Orleans still reached from modules". No new package references were added.
10. `GrainFactoryUseOnlyShrink` and `OrleansPackageReferencesOnlyShrink` are deleted, with `GrainFactoryOwners.json` and `OrleansReferenceOwners.json`. Baselines for those two were not regenerated.

Addressing that is not a mapping exception: relative `Neuron<T>("apps/assistant")` is `IdOf(contract, "{brain}/apps/assistant")`. `CompiledTypes.Assemblies` carries both `typeof(DigitalBrain.Signal).Assembly` and `typeof(IBrain).Assembly`.

`src/IntoChat.OS` still holds `Runtime/` (IBrain, Trace, ILogic, ISignalWatch, the string-key proxy extension, refusals), `Hosting/IntoChat.OS.Framework/`, `Contracts.Compute/`, and `Contracts.Configuration/`. The `Neurons`, `Signals`, `Synapses`, and `Kernel` folders and `NeutralAttributes.cs` are gone. The project is not empty, so it stays.

### Step 2 — exceptions

`DigitalBrain.Testing.TestOS` has `Instances`, `Types`, and `Brains` with the same shapes as `IBrainObserver` and does not implement the interface. `CoreHasNoOrleansFacts` forbids the `Orleans` namespace segment and an Orleans reference in that assembly. Step 3 still adds synapse listing and delivery subscription on `IBrainObserver`.

### Orleans still reached from modules

These call sites still take `IGrainFactory`, reminders, or Orleans `RequestContext`. Step 6 decides the allow-list. `TokenRequestContext` in `SessionRunner` is Azure identity, not Orleans.

- Kernel composition: `Kernel`, `KernelInvocationSink` (`RequestContext.Set`), `KernelGrains` (`RequestContext.Remove`), `GrainNeuronResolver`, `AuthorityVaultPort`, `PlainInstanceRegistration`, `RuntimeCallerMirror` (`RequestContext.Remove`), `PlainNeuronCallFilter` (`RequestContext` get/set), `ConfirmationNeuron` (`RequestContext.Get`), `KernelTestHooks` (`IGrainFactory`, `IRemindable`).
- AI: `BrainAwareCredentials`. Tests: `ProviderIntegrations`, `BrainKeyFacts`, `AutomaticChoiceFacts`, `CredentialFacts`.
- Flutter: `SpaceNeuron` and `SpaceWatcher` (`RequestContext`). Test: `SpaceFacts`.
- CSharp: `RuntimeEdge` (`IGrainFactory`), `BrainRuntimeNeuron` (`RequestContext`). Tests: `RuntimeEdgeBinderFacts`, `SourceWatchRaceFacts`.
- Circle.Server: `EnrollmentEndpoints`, `MachineAuthenticationHandler`.

### cutover(2): one vocabulary

Deleted 19 files, 571 lines: `IIntoChatOS`, `IKernelView`, `NeuronId`, `Signal`, `Synapse`, `NeutralAttributes`, the other neuron/signal/kernel types, `SignalWireFacts`, and the two ratchet JSON files.

`dotnet build Intochat.OS.slnx -c Release`: 0 errors, 0 warnings.

Tests: `IntoChat.OS.Kernel.Tests.Unit` 464 passed. `IntoChat.OS.Client.Tests.Unit` 40 passed. `IntoChat.OS.Modules.Microsoft.CSharp.Tests.Unit` 19 passed. `IntoChat.OS.Modules.Flame.Tests.Unit` 15 passed. Architecture facts for the vocabulary ring, the Orleans ban leftovers, signal loops, the observer path, static caches, grain interfaces, and Orleans attributes passed. The rest of `IntoChat.OS.Architecture.Tests.Unit` still has failures from earlier step 2 commits (deleted Framework test projects, stripped Contract serializers, collection and contract-manifest pins). Those pins were not regenerated.

### Step 2 status

The old neuron, signal, synapse, and kernel vocabulary is gone. Callers use DigitalBrain neurons, `NeuronId`, `Signal`, `INeuronProxies`, `ISignalWatch`, and `IBrainObserver`. `src/IntoChat.OS` keeps `Runtime/`, the moved host, and the two contract projects. Module projects that still call Orleans still reference it. `OrleansHostWiring`'s cloud storage overlay and `IClusterMembershipFeed` stay; their callers are still live.

## Step 3 — one app path

### cutover(3): move IBrainObserver to Spi and list synapses

`IBrainObserver` moved from `DigitalBrain.Runtime.Orleans` to `framework/src/DigitalBrain/Spi/IBrainObserver.cs`. Core has no Orleans types. `ClusterBrainObserver` still implements it. `TestOS` implements it. `Synapses` on `TestOS` is an explicit implementation because the class already has a `Synapses` registry property.

Added `ObservedSynapse` and `SignalDelivery`. `Synapses` scans the per-brain journal state named `synapses` (`BrainLog.Named`, `SynapseObservation.List`), skips the brain-wide sentinel, and reads the subscriber from the grain id (`neuron.host/`, `neuron.host.reentrant/`, `neuron.host.stateless/`). `Deliveries` is an in-memory fan-out with no replay. `SynapseEngine.React` notes a delivery after a successful reaction when the source is not brain-wide and the signal is not `Gap`. `TestOS` notes from `SynapseRegistry.React`. Product `Kernel` resolves `BrainLogs` and `DeliveryHub` from the service provider passed to `Kernel.For`. Empty when that provider is missing. `AddDigitalBrainRuntime` registers `DeliveryHub` before `IBrainObserver`. `AddIntoChatOS` still replaces `IBrainObserver` with `Kernel`.

`HostDispatchAccessFacts` looks up `IInstanceDirectory` on `typeof(StateHome).Assembly` because `typeof(IBrainObserver).Assembly` is now `DigitalBrain`.

`AppSource.Shipped` is unchanged. The script marketplace still names folder scripts with that source.

Outside this worker's area, required for the move: `using DigitalBrain.Spi` on Aspire client hosting, Authority `NeuronsService`, Orleans `BrainClient` and `OrleansBrain`, Connections `NeuronTypeSearch`, MCP caller/hosting/identity facts/`FixedKernel`, Tools `ToolCapabilitySource`, `IKernel`, `TypeAdded`, and the harness files `VocabularyRingFacts`, `ClientCompositionFacts`, `AspireBrain`, `AspireTestSession`, `ITestBrain`, `InProcessTarget`. `HostDispatchAccessFacts` is the one-line assembly lookup. `Kernel.cs` and `BrainHosting.cs` gained the implementation and the registration. `FastPathFacts.TimedKernel` forwards the two new methods. `CSharpFacts.FakeKernel` throws `NotSupportedException` for them.

Tests: `DigitalBrain.Testing.Tests.Unit` filter `ObserverFacts|ReactionDeliveryFacts` 10 passed, 0 failed. `DigitalBrain.Runtime.Orleans.Tests.Unit` filter `SynapseObservationFacts` 1 passed, 0 failed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.

### cutover(3): drop the compiled-app catalogs

Deleted the built-in packer, the prebuilt package catalog, and the uploaded compiled-package installer. A feature loads from its sources. `compiled-apps/` is gitignored; the local tree was removed. `PackageDigest` stays on `AppOrigin` so stored rows still read. Nothing assigns a package from it.

The hard-coded install lists in `OsAppsHosting` and `OsWorkspaceRoot.Connect` are gone. `OsAppsOptions` still exists and starts empty; a deployment can add apps. `OsInoMigration` is gone. `OsBootFacts` and `OsShippedAppFacts` only pinned those lists and were deleted. `TestTierBaseline.json` still names `OsInoMigrationFacts.cs`. That file is in the harness; step 6 owns the ratchet. The fact fails until that line is removed.

`FeatureDirectory.IsCompiledSource` stays until Files leaves `features/os`. The script compiler would otherwise compile `compiled/` and `hosting/`.

Contract records `CompiledAppReview` and `CompiledAppDevelopmentLease` stay so `ContractManifest.json` (step 6) still matches. Nothing calls them.

`AppSource.Shipped` stays. Folder scripts in the marketplace still use that source. `ShippedApps.Is` stays until Workspace, Desktop, and Settings are in-process apps.

Tests: `IntoChat.OS.Modules.Apps.Tests.Unit` 35 passed, 0 failed. `FeatureCompileFacts` 3 passed, 0 failed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings. `FeatureReconcilerFacts` still fails to boot a silo with `CodecNotFoundException` for `PriceQuote`. That failure is from step 2 stripping Contract serializers. It was not rerun as a pass.

### cutover(3): add the brain scene and default script folder

`[App("brain")] BrainScene` lives in the Apps module. `Compose` places one `sphere` per neuron on a circle and one `path` per synapse whose ends are those neurons. A `SignalDelivery` adds a repeating `particles` tween along the matching path. `NextZoom` multiplies the camera zoom by 1.5 up to 4. `BrainSceneHost` installs the app on brain activation (empty program, operation `open`), and on `AppRequested` `open` reads `IBrainObserver` and sets `IScene` at `apps/brain/scenes/graph`. A tap on that scene zooms. Deliveries update particles on a separate pump that re-stamps the caller who opened the scene. `AppsEndpoints.Key` was renamed `AppKey` because an `[App]` in this assembly makes a static `Key` method a DB0026 error.

`AppsOptions.Features` unset now loads `<Home>/apps` (`StateHome.Apps`). The directory is created only for that default. Hosts and tests that set `Features` keep their folder. Calculator, FilmCraft and PhotoCraft stay in `features/os` as examples; they are not copied into the home folder.

`FeatureDirectory.IsCompiledSource` still stays until Files leaves `features/os`. `ShippedApps` still stays until Workspace, Desktop and Settings are in-process apps. `AppSource.Shipped` stays for folder scripts.

Tests: `IntoChat.OS.Modules.Apps.Tests.Unit` 38 passed, 0 failed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.

### cutover(3): move Files into the Apps module

`features/os/Files` is gone: `app.cs`, `open.cs`, `FilesEntry.cs`, `hosting/`, and the `compiled/` project. `FilesApp` and the workspace neuron now compile in the Apps module. `FilesHost` installs the empty-program app on activation and forwards `AppRequested` to `IFilesWorkspace`, then presents the same side, main and full views. `FeatureDirectory.IsCompiledSource` is gone with that folder. `IFilesWorkspace` stays: it is the grain the in-process host calls, and the neuron still publishes the Flutter trees through it.

`OsFilesEntryFacts` was deleted with `FilesEntry`. The same checks live in `FilesRequestFacts`. The kernel unit project no longer links `FilesEntry.cs`. `Intochat.OS.slnx` no longer lists `IntoChat.Files`.

A host without the C# runtime neuron logs that the install did not finish: an empty program still asks that runtime to retire. Hosts that load the C# module install the app.

`ShippedApps` stays until Workspace, Desktop and Settings are in-process apps. `AppSource.Shipped` stays for folder scripts. Ino, Memory and the other `features/os` apps other than Calculator, FilmCraft and PhotoCraft are still on disk.

Tests: `IntoChat.OS.Modules.Apps.Tests.Unit` 45 passed, 0 failed. `FeatureCompileFacts` 3 passed, 0 failed (18 features). `FeatureDirectoryFacts` and `AppDeclarationFacts` 20 passed, 0 failed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.
