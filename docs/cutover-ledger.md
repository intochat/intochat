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

## Step 4 — one identity

### cutover(4): one identity module, drop marketplace workshop and pricing

Deleted 119 files, 10713 lines. `src/IntoChat.OS.Authority` and `src/IntoChat.OS.Connections` are gone. That includes Marketplace, Workshop, Compute, Enforcement, pricing, the Authority integration tests, and the Connections unit tests. `AuthorityVaultPort` and `AuthorityDirectoryPort` are gone. `KernelCapabilityConnections` stays: the broker still asks it for a brain's connections.

Ported into `src/IntoChat.OS.Modules/IntoChat.OS/Identity`: identity, secrets, connections, and the script contracts (`IntoChat.OS.Modules.Identity.Contracts`, so behaviors still compile against `IntoChat.OS.Integrations`). The module provides `identity@1` and `secrets@1`. `IdentityNeuron` and `SecretsNeuron` are `[Neuron]` types. The kernel registers `VaultPort` and `DirectoryPort` once. Account, principal, brain-authority, and vault state is still on the grains those ports call. Custody (`BrainStoreCustody`, master key) is unchanged.

Call sites that named the dropped types: Flutter `ComputeNeuron` returns an empty 30-day window. Circles no longer call `ICircleShares`. Apps marketplace list is empty and upsert throws `NotSupportedException` ("Marketplace returns in 0.8.0."). `AddMeteredHttpClient` adds no handler when `ICostMeter` or `IExternalCallContextAccessor` is absent. Deleted `MarketplaceClientFacts` and `MarketplaceRecordFacts` (they only proved the dropped catalog). Removed the `IWorkshop` assertion from `CompiledTypesFacts`.

`dotnet build Intochat.OS.slnx -c Release`: 0 errors, 0 warnings.

Tests: `IntoChat.OS.Kernel.Tests.Unit` filter `AuthorityVaultNamed|KernelCapability` 9 passed, 0 failed.

Still open in this step: collapse `CallerContext`, `KitCaller`, `UiEventCaller`, `AuthCaller`, `SessionMember`, and both `SessionView`s onto `CapabilityCaller`; replace the Authority cookie issuer with `DigitalBrain.Runtime.Http.ISessionIssuer`; move directory and vault state onto `Neuron<TState>`.

## Step 5 — one harness

### cutover(5): one harness on DigitalBrain.Testing

`DigitalBrain.Testing` (`TestOS`, `NeuronHarness`, `SiloHost`, the storage/secrets/identity kits) is the harness. The silo adapter is the unpackable project `framework/src/DigitalBrain.Testing.Silo` (namespace `DigitalBrain.Testing`). It holds `BothRuntimesFixture`, `INeuronRuntime`, and `NeuronRuntimeKind`, and it references `DigitalBrain.Runtime.Orleans` so `DigitalBrain.Testing` itself stays free of Orleans. `ModuleOnTestOSFacts<TModule>` is public on `DigitalBrain.Testing`. `BrowserSession` from the Playwright harness now lives in `IntoChat.OS.Modules.Microsoft.Playwright.Tests.Browser`. The Aspire `BrainBrowser` opener was deleted with the Aspire host.

Deleted: 666 files, 73314 lines (`git diff --numstat --diff-filter=D`).

Harness trees and their integration projects: 101 files, 6259 lines.

| Tree | Files | Lines |
| --- | ---: | ---: |
| `src/Testing/IntoChat.OS.Testing` | 35 | 2323 |
| `src/Testing/IntoChat.OS.Testing.Aspire` | 15 | 1404 |
| `src/Testing/IntoChat.OS.Testing.Tests.Integration` | 15 | 810 |
| `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Testing` | 14 | 519 |
| `src/Testing/IntoChat.OS.Testing.Aspire.Tests.Integration` | 6 | 402 |
| `src/Testing/IntoChat.OS.Testing.Playwright` | 5 | 342 |
| `src/Testing/IntoChat.OS.Testing.Neurons` | 5 | 275 |
| `src/Testing/IntoChat.OS.Testing.Fakes` | 3 | 106 |
| `src/Testing/IntoChat.OS.Aspire.Testing` | 3 | 78 |

`Directory.Build.props` no longer imports `IntoChat.OS.Testing`, `IntoChat.OS.Testing.Aspire`, or `IntoChat.OS.Kernel.Testing`. Test projects that still exercise behaviour reference `DigitalBrain.Testing` or `DigitalBrain.Testing.Silo`.

Ported onto the new harness:

- `BothRuntimesFixture` moved into `DigitalBrain.Testing.Silo`. Time, AI, ArtCraft, Flutter, Flame, Roslyn, Mcp, and CSharp integration projects use it.
- `ModuleOnTestOSFacts<TModule>` plus a subclass in each module unit project that has a `[Module]` type: AI, ArtCraft, ClickHouse, Drive, Flutter, Gmail, Apps, Circles, Flame, Aspire, Azure, CSharp, GitHub, Playwright, Roslyn, Windows, Mcp, Postgres, Salesforce, Time.
- `*ModuleSiloFacts` (`SiloHost.Local`) for integration projects that had no `BothRuntimesFixture`: ClickHouse, Drive, Gmail, Apps, Circles, Aspire, Azure, GitHub, Playwright, Windows, Postgres, Salesforce.
- `WindowsStorageKit : StorageConformance<IStorage>` for `storage@1` on `WindowsModule`. Windows unit: 50 succeeded, 1 skipped.
- `BrowserSession` in the Playwright browser project. That project passed.

Tests deleted because they reference Authority, Connections, BuiltinApps, compiled apps, or `features/os` apps other than Calculator, FilmCraft, and PhotoCraft (83 files, 9362 lines). FilmCraft and PhotoCraft had no tests in this deletion set. `OsCalculatorAppFacts` was deleted with the kernel harness (`SolutionProjects`); the Calculator app source was left for step 3.

Authority integration (the whole `IntoChat.OS.Authority.Tests.Integration` tree that this step removed):

- `AccountingRecoveryFacts.cs`, `AuthorityHost.cs`, `AuthorityRenameFacts.cs`, `BrainGrains.cs`, `CircleReservationFacts.cs`, `CircleShareGuardFacts.cs`, `ComputeReaderFacts.cs`, `ComputeReservationFacts.cs`, `ComputeTraceEndpointFacts.cs`, `CostMeterFacts.cs`, `ExternalCallFacts.cs`, `ExternalCallTransportFacts.cs`, `LedgerFacts.cs`, `MasterKeyWrapperFacts.cs`, `PlatformHostingFacts.cs`, `SecretsFacts.cs`, `TraceJournalFacts.cs`, `UsageFacts.cs`, `WorkshopSandboxFacts.cs`
- `Connections/`: `ConnectionIndexFacts.cs`, `ConnectionNeuronFacts.cs`, `ConnectionRouteFacts.cs`, `ConnectionSharingFacts.cs`, `ExampleConnectionModule.cs`, `GalleryConnectFacts.cs`, `LegacyStorageAdapterFacts.cs`, `OAuthFlowFacts.cs`, `SecretSealerFacts.cs`, `TwoConnectorFacts.cs`
- `Entries/BrainEntriesFacts.cs`
- `Hosting/SiloDiagnosticsFacts.cs`
- `Identity/`: `AccountFacts.cs`, `AuthPostureFacts.cs`, `BrainEstablishmentFacts.cs`, `BrainMembershipFilterFacts.cs`, `BrainOwnerDispatchFacts.cs`, `DefaultBrainFacts.cs`, `DeploymentStorageIdentityFacts.cs`, `ExternalSignInFacts.cs`, `FrozenCryptoContextFacts.cs`, `GrantFacts.cs`, `IdentityHostingFacts.cs`, `KernelCorsFacts.cs`, `MarketplaceAuthorityFacts.cs`, `NeutralDefaultsFacts.cs`, `OwnerProvisioningFacts.cs`, `PasswordEndpointSwitchFacts.cs`, `PrivilegedModuleGateFacts.cs`, `RemovePrincipalMemberFacts.cs`, `RuntimeManagementFacts.cs`, `ScopedAuthorityFacts.cs`, `SecretOwnershipFacts.cs`, `SessionCharacterizationFacts.cs`, `SessionIssuerFacts.cs`, `SignInHandlerFacts.cs`
- `Integrations/`: `CallerContextScopeFacts.cs`, `ConnectorBindingFacts.cs`, `IntegrationSeederFacts.cs`, `RegistrationFacts.cs`, `ScriptAccountFacts.cs`
- `Keys/MasterKeyCustodyFacts.cs`
- `Neurons/`: `BrainCallFacts.cs`, `ConfirmationWatchFacts.cs`, `NeuronsCallFacts.cs`, `NeuronsLimitsFacts.cs`, `NeuronsRouteFacts.cs`, `NeuronsWatchFacts.cs`, `PlainWatchFacts.cs`, `SelectedBrainFacts.cs`, `SignalTypesFacts.cs`
- `Storage/`: `BrainGrainStorageFacts.cs`, `BrainStorageFacts.cs`, `SegmentSealFacts.cs`

Connections, BuiltinApps, compiled apps:

- `src/IntoChat.OS.Connections/IntoChat.OS.Connections.Tests.Unit/ConnectionsHostingFacts.cs`
- `src/IntoChat.OS.Connections/IntoChat.OS.Connections.Tests.Unit/SecretRedactorFacts.cs`
- `src/IntoChat.OS.BuiltinApps/IntoChat.OS.BuiltinApps.Tests.Unit/BuiltinPackageFacts.cs`
- `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Testing/Workspace/UserConnections.cs`
- `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Tests.Unit/Capabilities/KernelCapabilityConnectionsFacts.cs`
- `Apps/CompiledAppAssignmentFacts.cs`, `CompiledAppInstallFacts.cs`, `CompiledAppDevelopmentFacts.cs`, `CompiledAppPackagesFacts.cs`, `CompiledAppReviewFacts.cs`

`features/os` app tests deleted with the harness (Ino, Memory, SelfImprove, Assistant, Friends, Router):

- Kernel unit: `AssistantViewFacts.cs`, `OsAssistantAppFacts.cs`, `OsInoChatCommandsFacts.cs`, `OsInoChatsFacts.cs`, `OsInoIntentFacts.cs`, `OsInoLinkFacts.cs`, `OsInoMigrationFacts.cs`, `OsInoModelFacts.cs`, `OsInoSecretFacts.cs`, `OsInoToolsFacts.cs`, `OsInoViewFacts.cs`, `OsMemoryEpisodeFacts.cs`, `OsMemoryIntendFacts.cs`, `OsMemoryPromptFacts.cs`, `OsMemoryRecallFacts.cs`, `OsSelfImproveAppFacts.cs`, `FriendsPostAdapter.cs`, `FriendsReplayFacts.cs`
- Kernel integration: `InoOpenBackfillFacts.cs`, `InoScanRecoveryFacts.cs`, `MemoryIntendRecoveryFacts.cs`, `SelfImproveAvailabilityFacts.cs`
- Kernel E2E: `AssistantGateFacts.cs`, `InoChatFacts.cs`, `InoSecretFacts.cs`, `IntentLoopFacts.cs`, `MemoryLoopFacts.cs`, `SelfImproveFacts.cs`, `SelfImproveHostFixture.cs`, `RouterHostFacts.cs`, `RouterHostFixture.cs`
- AI integration: `Scenarios/AssistantGateFacts.cs`, `Scenarios/AssistantScenarioFacts.cs`, `Scenarios/ShippedAssistant.cs`
- Flutter integration: `Space/FriendsSpaceFacts.cs`
- Circles integration: `FriendsPublicationFacts.cs`

Also deleted, because they only proved the old plumbing or a pre-step-2 wire pin: DI descriptor checks, assembly inventories, service-type names, product HTTP/`Person`/`AppHost` journeys, and the Orleans serializer pins `FlutterWireNameFacts` (plus `flutter-wire-names.golden.json` and `flutter-aliasless-enums.golden.json`) and `SolutionMapCompatibilityFacts`. `*OnlyShrink` fact sources and `OrleansAttributeOwners.json` were left for step 6. That JSON still names `FlutterWireNameFacts.cs`.

`dotnet build Intochat.OS.slnx -c Release` before this commit: Build succeeded. 0 Warning(s). 0 Error(s). Time Elapsed 00:00:05.33.

Tests, from the per-project logs under `%TEMP%\cutover5-test-*.log`, with Flutter integration, Roslyn unit, Windows unit, Time unit, and Time integration replaced by their reruns after the pin deletions: 1563 passed, 2 failed, 20 skipped. 77 logs. 17 projects ran zero tests and exited 8 (empty after the harness deletion; the projects stay in the solution for step 6): Cloud.Auth integration, Circle.Server integration, Aspire.Hosting E2E, Authority integration, BuiltinApps unit, CLI integration, Client.Orleans integration, Connections unit, Kernel E2E, Kernel integration, Mcp integration, AI hosting, GitHub hosting, Orleans hosting, Postgres hosting, Supabase hosting, Supabase integration. `IntoChat.OS.Modules.Google.Gmail.Tests.Support` is a support library; the runner reported "No test projects were found" (exit 1).

The 2 failures are `IntoChat.OS.Aspire.Hosting.Tests.Unit` (60 passed, 2 failed): `DrainOrderFacts.TheDrainStopsTheServerInStoppingAsyncBeforeAnyHostedServiceStops` and `ConfigurationFileFacts.ConfigurationFileIsLoadedWhenTheKeyIsSet`. `LegacyConfigurationGuard.Verify` throws because this machine still has `%LOCALAPPDATA%\DigitalBrain`. Production guard code was left as it is.

Cited reruns: Flutter integration 27 passed; Roslyn unit 9 passed; Windows unit 50 passed, 1 skipped; Time integration 21 passed; Time unit 9 passed.

End state: the seven harness directories are gone. `rg "TestBrain\.Create|ReferenceBrain" -g *.cs src` is 2 hits, both in `IntoChat.OS.Architecture.Tests.Unit` (`CredentialBoundaryFacts.cs:107`, `TestTierFacts.cs:110`). Those fact strings stay for step 6.

### Step 5 status

Deleted 666 files, 73314 lines, including the eight harness trees and their two integration projects (101 files, 6259 lines). Ported the dual-runtime fixture into unpackable `DigitalBrain.Testing.Silo`, added `ModuleOnTestOSFacts`, a Windows `storage@1` kit, per-module TestOS and SiloHost facts, and `BrowserSession` in the Playwright browser project. The build broke on missing harness types; plumbing and pin tests were deleted and behaviour tests were pointed at `TestOS` and `SiloHost`. The architecture project lost its `IntoChat.OS.Testing` reference so the solution compiles. Release build 0 errors, 0 warnings. Touched tests: 1563 passed, 2 failed, 20 skipped; 17 projects ran zero tests. Deferred to step 6: `ModuleTestTierFacts.InProcessHosts` still names the deleted harness projects, the two `ReferenceBrain` fact strings, `*OnlyShrink`, and `OrleansAttributeOwners.json`. Drive `storage@1` has no kit because Drive storage still goes through Connections. Cloud.Auth, Authority, and Kernel.Composition have no unit project, so they have no `ModuleOnTestOSFacts`. The two Aspire hosting failures are this machine's leftover `%LOCALAPPDATA%\DigitalBrain` directory.

### Outside this step's area

- `src/Testing/IntoChat.OS.Architecture.Tests.Unit/IntoChat.OS.Architecture.Tests.Unit.csproj` — removed the `ProjectReference` to `../IntoChat.OS.Testing/IntoChat.OS.Testing.csproj`. No fact source was edited.
- `Intochat.OS.slnx` — removed the deleted harness projects and `IntoChat.OS.Kernel.Testing`; added `DigitalBrain.Testing.Silo`.
- `framework/DigitalBrain.slnx` — added `DigitalBrain.Testing.Silo` so the framework solution lists every framework project.

## Merge

### cutover(merge): identity

Conflicts, per file:

- `docs/cutover-ledger.md`: kept Step 3 and Step 4, in that order.
- `src/IntoChat.OS.BuiltinApps/IntoChat.OS.BuiltinApps/IntoChat.OS.BuiltinApps.csproj`: step 3 deleted the project and step 4 edited the project file. The project stays deleted.
- `src/IntoChat.OS.Modules/IntoChat.OS/Apps/src/IntoChat.OS.Modules.Apps/IntoChat.OS.Modules.Apps.csproj`: dropped the `IntoChat.OS.Connections` project reference. Dropped `IncludeCompiledFiles`, which packaged `IntoChat.Files.dll`. Step 3 moved Files into the Apps module, so that target has no assembly. Kept the Identity module reference from step 4 and the Flame contracts reference from step 3.

References re-pointed:

- `Intochat.OS.slnx` no longer lists Authority, Connections, BuiltinApps, or `features/os/Files`. It lists `IntoChat.OS.Modules.Identity` and `IntoChat.OS.Modules.Identity.Contracts`.
- `IntoChat.OS.Kernel.csproj` no longer references `IntoChat.OS.Authority`. The module glob references the Identity project. The `features/os` pack item stays removed with step 3.
- Call sites that named Authority and Connections compile against the Identity module. That module keeps the `IntoChat.OS.Authority` and `IntoChat.OS.Connections` namespaces. `using DigitalBrain.Spi` on the moved `NeuronsService` and `NeuronTypeSearch` survived the rename.

`CapabilityKitCoverageFacts` counts `identity@1` and `secrets@1` as covered by the kernel integration kits (`IdentityCapabilityConformance`, `SecretsCapabilityConformance`). The identity module declares both capabilities. Step 4 did not add a module test project.

`dotnet build Intochat.OS.slnx -c Release`: 0 errors, 0 warnings.

Tests:

- `IntoChat.OS.Kernel.Tests.Unit`: 442 passed, 0 failed.
- `IntoChat.OS.Modules.Apps.Tests.Unit`: 45 passed, 0 failed.
- `DigitalBrain.Tests.Unit`: 191 passed, 1 failed.
- `DigitalBrain.Testing.Tests.Unit`: 316 passed, 0 failed, 17 skipped.

Left failing: `DigitalBrain.Tests.Unit.CoreVocabularyFacts.Spi_is_interfaces_only`. `ObservedSynapse` and `SignalDelivery` are records in `DigitalBrain.Spi`. Step 3 added them. The fact still requires every `DigitalBrain.Spi` type to be an interface or a static class. The identity merge did not change that fact or those records.

### cutover(merge): harness

Conflicts, per file:

- `docs/cutover-ledger.md`: kept Step 3, Step 4, Step 5, then this Merge section.
- `IntoChat.OS.Kernel.Tests.Unit.csproj`: dropped the `features/os` `Compile` links and the `IntoChat.OS.Testing` reference. Kept `DigitalBrain.Testing`, the kernel, LocalSetup, and AppHost. Step 5 already removed `IntoChat.OS.Kernel.Testing` and the kit-copy target.
- Stayed deleted, because step 5 deleted them and step 3 had edited them: `HostDispatchAccessFacts.cs`, `CapabilityKitCoverageFacts.cs`, `McpIdentityFacts.cs`, `GmailFaultControl.cs`, `AppDeclarationFacts.cs` (integration), `FastPathFacts.cs`, `FeatureDirectoryFacts.cs`, `FilesRequestFacts.cs` (the step 3 rename of `OsFilesEntryFacts`), `CircleFacts.cs`, `CompiledTypesFacts.cs`, `RunFactsFacts.cs`, `CSharpFacts.cs`, `ClientCompositionFacts.cs`, `AspireBrain.cs`, `AspireTestSession.cs`, `PersonFacts.cs`, `ITestBrain.cs`, `InProcessTarget.cs`.
- Stayed deleted, because step 3 or step 4 deleted the projects and step 5 edited the project files: `IntoChat.OS.Authority.Tests.Integration.csproj`, `IntoChat.OS.BuiltinApps.Tests.Unit.csproj`, `IntoChat.OS.Connections.Tests.Unit.csproj`.

References re-pointed:

- Test projects that used `IntoChat.OS.Testing` or `IntoChat.OS.Testing.Neurons` reference `DigitalBrain.Testing` or `DigitalBrain.Testing.Silo`. `Intochat.OS.slnx` and `framework/DigitalBrain.slnx` list `DigitalBrain.Testing.Silo`. The harness trees and `IntoChat.OS.Kernel.Testing` are gone.
- `IntoChat.OS.Modules.Circles.Tests.Integration.csproj` references the Identity module. The Authority project reference from step 5 does not come back. The `Using` of `IntoChat.OS.Testing` is gone.

`dotnet build Intochat.OS.slnx -c Release`: 0 errors, 0 warnings.

Tests:

- `IntoChat.OS.Kernel.Tests.Unit`: 21 passed, 0 failed.
- `IntoChat.OS.Modules.Apps.Tests.Unit`: 31 passed, 0 failed.
- `DigitalBrain.Tests.Unit`: 191 passed, 1 failed.
- `DigitalBrain.Testing.Tests.Unit`: 316 passed, 0 failed, 17 skipped.

Left failing: the same `Spi_is_interfaces_only` fact. Step 5 did not change `CoreVocabularyFacts` or the two Spi records.

## Step 6 — Ratchets

`cutover(6): spi records to core` (`b41256505`) moved `ObservedSynapse` and `SignalDelivery` from `DigitalBrain.Spi` to `DigitalBrain`, next to `Synapse`. `IBrainObserver` stays in `Spi`. `Spi_is_interfaces_only` is green after that commit.

Deleted 39 files, 4212 lines. That is every `*OnlyShrink` fact, every `Capture*` fact, `ContractOwnershipCompatibilityFacts`, and every architecture pin JSON except `ContractManifest.json` (regenerated from the current capture). Scripts deleted: `scripts/capture-all.ps1`, `scripts/capture-all.sh`, `scripts/keys-only-check.ps1`. `CONTRIBUTING.md` no longer points at those scripts. `framework/tests/DigitalBrain.Architecture.Tests.Unit` was not edited. No Dart fact was added. `ShippedApps` / `AppSource.Shipped` stay for the folder scripts; no fact was added about them.

`ModuleTestTierFacts.InProcessHosts` is `DigitalBrain.Testing.Silo`. `TestTierBaseline.json` and the `ReferenceBrain` token are gone. The provider-key fact is `OnlyTheAiModuleAndTheIdentityModuleTouchBrainProviderKeys`, so the source does not contain the substring `ReferenceBrain`. The 24 concrete persisted collections live in `PersistedStateRules.UnmigratedConcreteCollections` (the set does not grow). Drive `storage@1` stays special-cased: there is still no in-process kit.

Orleans reach (`OrleansBanFacts`): the shrinking allow-list is exactly `IntoChat.OS.Kernel.Composition`, `IntoChat.OS.Modules.AI`, `IntoChat.OS.Modules.Flutter`, `IntoChat.OS.Modules.Microsoft.CSharp`, `IntoChat.Cloud.Circle.Server`. The comment on that array says it shrinks to zero in 0.8.0. Identity and Apps are not on that list. They are a separate closed pair, `UnportedGrainModules`, because they still compile grains; they leave in 0.8.0 with the five. The silo process (`IntoChat.OS`, `IntoChat.OS.Framework`, `IntoChat.OS.Client`, `IntoChat.OS.Client.Orleans`, `IntoChat.OS.Aspire.Server`, `IntoChat.OS.Aspire.Client`) is a separate permanent set so the kernel can boot. It is not the shrinking allow-list. `[GenerateSerializer]` is wire format and is not this ban. Unused `using Orleans` imports were removed from Circles, Postgres, and GitHub. Gmail and the Aspire module dropped the Reminders package they no longer call.

Identity gained `IntoChat.OS.Modules.Identity.Tests.Unit` (TestOS plus the secrets and identity conformance kits) and `IntoChat.OS.Modules.Identity.Tests.Integration` (`SiloHost.Local`, home in a temp directory). `IntoChat.OS.Kernel.Composition.Tests.Integration` references `DigitalBrain.Testing.Silo`.

`dotnet build Intochat.OS.slnx -c Release`: Build succeeded. 0 Warning(s). 0 Error(s). Time Elapsed 00:00:07.27.

Tests:

- `IntoChat.OS.Architecture.Tests.Unit`: 205 passed, 0 failed.
- `IntoChat.OS.Modules.Identity.Tests.Unit`: 39 passed, 0 failed.
- `IntoChat.OS.Modules.Identity.Tests.Integration`: 1 passed, 0 failed.
- `IntoChat.OS.Kernel.Composition.Tests.Integration`: 3 passed, 0 failed.

`TestShardFacts` and `.github/test-shards.json` stay until step 7. The Flutter integration pin in that JSON is the only remaining pin.

### Step 6 status

Deleted 39 files, 4212 lines: the shrink ratchets, the capture facts, and every pin JSON except `ContractManifest.json`. Ported nothing new. The architecture suite was red on stale pins, deleted project names, and unlogged catches; the pins were deleted or recaptured, the identity module got its two test projects, and a catch that classifies or logs its exception now satisfies `SwallowFacts`. Release build 0 errors, 0 warnings. Touched tests: 248 passed, 0 failed. Deferred to step 7: shard JSON, the md5 bucket script, and `TestShardFacts`. Deferred to 0.8.0: the five-assembly Orleans allow-list, the Identity and Apps grains, and the silo-host assemblies (those hosts are not on the shrinking list).

## Step 7 — CI

Deleted 3 files, 220 lines: `.github/test-shards.json` (18), `src/Testing/IntoChat.OS.Architecture.Tests.Unit/TestShardFacts.cs` (112), `scripts/verify-single-runtime.py` (90). Nothing in the repo called the Python script. `scripts/dx04-drive.sh` stays: the `dx04-packages` job runs it. `gitleaks.yml` stays as its own workflow. The md5 `bucket()` script is gone from `ci.yml`.

`ci.yml` still builds `Intochat.OS.slnx` once (`-p:IntoChatOSNightly=false`), archives `artifacts/bin`, then runs one `dotnet test` per PR-gate tier. The matrix is Unit, Integration, Hosting. Each command is `dotnet test Intochat.OS.slnx -c Release --no-build -p:IntoChatOSNightly=false -p:IntoChatOSOnlyTier=<tier> -p:CodeGraphRefresh=false --filter "Tier=<tier>"`. `Directory.Build.props` stamps `Xunit.TraitAttribute("Tier", <suffix>)`. `IntoChatOSOnlyTier` drops every other tier from that invocation, because a project whose filter matches nothing exits 8. On this SDK a solution run still exits 0 when some modules are empty and at least one test ran. Framework test projects do not import the root props, so `framework/Directory.Build.props` treats a non-empty `IntoChatOSOnlyTier` as "not a test project". The `framework-*` jobs do not pass that property, so they still run. Empty hosting shells (AI, GitHub, Orleans, Postgres, Supabase) and empty integration projects stay in the solution; a Hosting run here executed Flutter's 3 tests and exited 0.

`e2e.yml` and `ci.yml` accept `workflow_call`. `release.yml` jobs are `ci`, `e2e-latest` (`uses: ./.github/workflows/e2e.yml`), and `release` with `needs: [ci, e2e-latest]`. No workflow was dispatched.

`dotnet build Intochat.OS.slnx -c Release -p:IntoChatOSNightly=false`: Build succeeded. 0 Warning(s). 0 Error(s). Time Elapsed 00:00:10.39.

Tests:

- `IntoChat.OS.Architecture.Tests.Unit`: 202 passed, 0 failed.
- Hosting tier, solution, `--filter Tier=Hosting`: 3 passed, 0 failed, exit 0. Five hosting projects have no tests and print exit code 8 per module; the solution summary is Passed.

### Step 7 status

Deleted 3 files, 220 lines: the shard pin, `TestShardFacts`, and `verify-single-runtime.py`. Ported nothing. The tier trait and `IntoChatOSOnlyTier` keep one `dotnet test` from failing on the other tiers, and framework tests stay on the `framework-*` jobs. Release build 0 errors, 0 warnings. Touched tests: architecture 202 passed, 0 failed; Hosting tier 3 passed, exit 0. Deferred to step 8: the end-state command block and the live kernel. Deferred to 0.8.0: the empty hosting shells, which are still projects with no facts.
