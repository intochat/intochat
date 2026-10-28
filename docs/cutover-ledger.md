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

### cutover(9): callers, directory and vault on neurons

`CallerContext`, `KitCaller`, `UiEventCaller`, `AuthCaller` and `SessionMember` are `DigitalBrain.CapabilityCaller`. Kind, stamped-by, person and on-behalf-of are extension members; `CapabilityCallers` encodes the edge in `AppId` (empty is the person, `platform` / `scheduler` / `assistant` are the trusted edges). Auth `SessionView` stays the device-list row. `GET /identity/session` returns `CapabilityCaller` and drops role; the CLI session role is optional. The os `ISessionIssuer` is gone. `AccountSessionIssuer` is `DigitalBrain.Runtime.Http.ISessionIssuer`. Cookie claims are `SessionClaims` only.

Directory state is `DirectoryNeuron` (`Neuron<DirectoryState>`, brain `system`, key `main`). Vault state is `VaultNeuron` (`Neuron<SecretsState>`). The account, principal, brain-authority and vault grains are deleted. Product modules reach identity and grants through `ICapabilityBroker`. The Identity module's own facade stays `IIdentityClient` over `DirectoryNeuron`. `SecretsState` keeps its field ids (`mydata.state`). `ContractManifest.json` drops the deleted grain identities and pins `DirectoryChanged` and `VaultChanged`.

Clean break: cookies that carried `os.account` or `NameIdentifier` do not resolve, and stored grain documents are not read. A caller cannot be an app and a platform stamp at once, because both facts are the one `AppId`. Platform vault release uses `AppId` `platform` and drops the previous app id.

`dotnet build Intochat.OS.slnx -c Release`: 0 errors, 0 warnings.

Tests: `IntoChat.OS.Architecture.Tests.Unit` 202 passed. `IntoChat.OS.Modules.Identity.Tests.Unit` 39 passed. `IntoChat.OS.Kernel.Tests.Unit` 21 passed. `IntoChat.OS.Modules.Apps.Tests.Unit` 32 passed. `IntoChat.OS.Modules.Circles.Tests.Unit` 24 passed. `DigitalBrain.Testing.Tests.Unit` 316 passed, 17 skipped. `DigitalBrain.Capabilities.Identity.Tests.Unit` 39 passed. `DigitalBrain.Runtime.Orleans.Tests.Unit` capability filter 67 passed. `IntoChat.OS.Cli.Tests.Unit` 32 passed. `IntoChat.OS.Modules.Flutter.Tests.Unit` 18 passed.

### cutover(9): local production session-token key

A configured csharp `RunTokenKey` still wins. Development with no key still mints an ephemeral key that dies with the process. Production (and any non-Development host) with `Topology` unset, `local`, `local+cloud` or `local-cloud` mints a 32-byte key on first start, protects it the same way as the master key, and stores it at `<Home>/secrets/session-token.key`. The next start reads that file. `Topology=cloud` still throws before listen when `RunTokenKey` is absent, and it does not create the file.

`dotnet build Intochat.OS.slnx -c Release`: 0 errors, 0 warnings. `IntoChat.OS.Modules.Microsoft.CSharp.Tests.Unit` 6 passed (5 of them are `SessionTokensFacts`).

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

## Step 8 — Live proof

The plan's bash block was run from PowerShell (`rg` in place of `grep`, `Test-Path` in place of `ls`). Counts are the plan's filters, case-sensitive, so a `tests` directory is not excluded by `Tests`.

```
=== vocabulary paths (expect absent) ===
absent src/IntoChat.OS/Neurons
absent src/IntoChat.OS/Signals
absent src/IntoChat.OS/Synapses
EXISTS src/IntoChat.OS/Runtime
absent src/IntoChat.OS.Framework
absent src/IntoChat.OS.Authority
absent src/IntoChat.OS.Connections
absent framework/src/DigitalBrain/Spi/LegacySpi.cs
=== harness paths (expect absent) ===
absent src/Testing/IntoChat.OS.Testing
absent src/Testing/IntoChat.OS.Testing.Aspire
absent src/Testing/IntoChat.OS.Testing.Neurons
absent src/Testing/IntoChat.OS.Testing.Fakes
absent src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Testing
EXISTS compiled-apps
absent src/IntoChat.OS.BuiltinApps
=== Microsoft.Orleans csproj outside runtime, Orleans module, Tests (expect 0) ===
count 13
=== using Orleans | IGrainWithStringKey | GenerateSerializer outside obj, Tests, Orleans module (expect 0) ===
count 127
=== Module< or new ModuleId( (expect 0) ===
count 16
=== ShippedApps.Is | AppSource.Shipped | CompiledBuiltinCatalog | ShippedPrograms (expect 0) ===
count 12
=== TestBrain.Create | ReferenceBrain (expect 0) ===
count 0
=== architecture json (expect <= 2) ===
count 1
ContractManifest.json
=== IntoChat.Cloud in kernel csproj ===
41:    <ProjectReference Include="../../IntoChat.Cloud/Modules/Auth/src/IntoChat.Cloud.Auth/IntoChat.Cloud.Auth.csproj" />
42:    <ProjectReference Include="../../IntoChat.Cloud/Modules/Circle/src/IntoChat.Cloud.Circle.Server/IntoChat.Cloud.Circle.Server.csproj" />
=== dotnet build Intochat.OS.slnx -c Release ===
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:09.53
=== curl -f http://localhost:5000/health ===
curl: (7) Failed to connect to localhost:5000 after 2264 ms: Could not connect to server
CURL_EXIT 7
```


Matching paths from those counts:

Orleans csproj:
- src\IntoChat.OS\IntoChat.OS.csproj
- src\IntoChat.OS.Aspire\IntoChat.OS.Aspire.Server\IntoChat.OS.Aspire.Server.csproj
- src\IntoChat.Cloud\Modules\Circle\src\IntoChat.Cloud.Circle.Server\IntoChat.Cloud.Circle.Server.csproj
- src\IntoChat.OS.Aspire\IntoChat.OS.Aspire.Client\IntoChat.OS.Aspire.Client.csproj
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel.Composition\IntoChat.OS.Kernel.Composition.csproj
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\IntoChat.OS.Framework.csproj
- src\IntoChat.OS.Client\IntoChat.OS.Client.Orleans\IntoChat.OS.Client.Orleans.csproj
- src\IntoChat.OS.Client\IntoChat.OS.Client\IntoChat.OS.Client.csproj
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\IntoChat.OS.Modules.Flutter.csproj
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\IntoChat.OS.Modules.AI.csproj
- src\IntoChat.OS.Modules\Microsoft\Aspire\src\IntoChat.OS.Modules.Microsoft.Aspire\IntoChat.OS.Modules.Microsoft.Aspire.csproj
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\IntoChat.OS.Modules.Identity.csproj
- src\IntoChat.OS.Modules\Microsoft\CSharp\src\IntoChat.OS.Modules.Microsoft.CSharp\IntoChat.OS.Modules.Microsoft.CSharp.csproj

using Orleans, IGrainWithStringKey, or GenerateSerializer:
- src\IntoChat.OS\Runtime\ILogic.cs
- src\IntoChat.OS.Client\IntoChat.OS.Client\Runtime\V1\RuntimeProtocol.cs
- src\IntoChat.OS.Aspire\IntoChat.OS.Aspire.Server\ServerHostingExtensions.cs
- src\IntoChat.OS\Runtime\Trace.cs
- src\IntoChat.OS\Runtime\State.cs
- src\IntoChat.OS\Runtime\StaleActionException.cs
- src\IntoChat.OS\Runtime\ServiceUnavailableException.cs
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel.Composition\Kernel\BrainHosting.cs
- src\IntoChat.OS\Runtime\Owner.cs
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel.Composition\Kernel\KernelInvocationSink.cs
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel.Composition\Kernel\PlainInstanceRegistration.cs
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel.Composition\Kernel\KernelGrains.cs
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel.Composition\Kernel\RuntimeCallerMirror.cs
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel.Composition\Kernel\Testing\KernelTestHooks.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Runtime\Placement\BrainAffinePlacement.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Runtime\NeutralCallMapping.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Runtime\Kernel\KernelGrains.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Runtime\Kernel\Feature.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Runtime\Hosting\LegacyTypeNames.cs
- src\IntoChat.OS.Modules\ArtCraft\ArtCraft\src\IntoChat.OS.Modules.ArtCraft\Contracts\IArtCraftStudio.cs
- src\IntoChat.OS.Modules\Salesforce\src\IntoChat.OS.Modules.Salesforce\Salesforce\SalesforceState.cs
- src\IntoChat.Cloud\Modules\Circle\src\IntoChat.Cloud.Circle.Server\MachineCredentialNeuron.cs
- src\IntoChat.OS.Modules\ClickHouse\src\IntoChat.OS.Modules.ClickHouse\Query\ClickHouseNeuron.cs
- src\IntoChat.OS.Modules\Salesforce\src\IntoChat.OS.Modules.Salesforce\Salesforce\SalesforceExceptions.cs
- src\IntoChat.Cloud\Modules\Circle\src\IntoChat.Cloud.Circle.Server\MachineAuthenticationHandler.cs
- src\IntoChat.Cloud\Modules\Circle\src\IntoChat.Cloud.Circle.Server\EnrollmentEndpoints.cs
- src\IntoChat.OS.Modules\Google\Drive\src\IntoChat.OS.Modules.Google.Drive\DriveNeuron.cs
- src\IntoChat.OS.Modules\Google\Drive\src\IntoChat.OS.Modules.Google.Drive\DriveModule.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Routing\RouterState.cs
- src\IntoChat.OS.Modules\Postgres\src\IntoChat.OS.Modules.Postgres\Tables\TablePlacement.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Routing\AppMenuHost.cs
- src\IntoChat.OS.Modules\Postgres\src\IntoChat.OS.Modules.Postgres\Tables\TableNeuron.cs
- src\IntoChat.OS.Modules\Postgres\src\IntoChat.OS.Modules.Postgres\Tables\TableIndexNeuron.cs
- src\IntoChat.OS.Modules\Postgres\src\IntoChat.OS.Modules.Postgres\Tables\TableDescriptor.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Gmail\GmailState.cs
- src\IntoChat.OS.Modules\Postgres\src\IntoChat.OS.Modules.Postgres\PostgresServerNeuron.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Gmail\GmailLifecycleState.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Gmail\GmailExceptions.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Gmail\GmailCredentialException.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Metering\IntentUsageNeuron.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Modules\Identity\CallerContext.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Modules\Identity\Brains.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Modules\Identity\Accounts.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Contracts\Gmail\IGmail.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Contracts\Gmail\GmailValues.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Contracts\Gmail\GmailResults.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Contracts\Gmail\GmailRequests.cs
- src\IntoChat.OS.Modules\Google\Gmail\src\IntoChat.OS.Modules.Google.Gmail\Contracts\Gmail\GmailDraftPreview.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Embeddings\VectorIndexNeuron.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Workspace\WorkspaceNeuron.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Workspace\ProblemReportsState.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Compute\TraceOutgoingFilter.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Workspace\ProblemReport.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Workspace\LiveSignalEndpoints.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Callers\ICallFilter.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Callers\CallRequest.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Views\ViewNeuron.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Callers\CallerContextStamper.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\FlutterModule.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Viewport\ViewportNeuron.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Agents\Intents\IntentsNeuron.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Clients\AIClients.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Agents\Facts\FactsNeuron.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\AIModule.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Agents\Context\AgentContextNeuron.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Chat\ComposerNeuron.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Chat\ChatIndexNeuron.cs
- src\IntoChat.OS.Modules\Microsoft\Windows\src\IntoChat.OS.Modules.Microsoft.Windows\Contracts\IWindows.cs
- src\IntoChat.OS.Modules\Microsoft\Windows\src\IntoChat.OS.Modules.Microsoft.Windows\WindowsNeuron.cs
- src\IntoChat.OS.Modules\Microsoft\Windows\src\IntoChat.OS.Modules.Microsoft.Windows\Contracts\IConsole.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Ports\VaultPort.cs
- src\IntoChat.OS.Modules\Microsoft\Roslyn\src\IntoChat.OS.Modules.Microsoft.Roslyn\Workspace\WorkspaceState.cs
- src\IntoChat.OS.Modules\Microsoft\Roslyn\src\IntoChat.OS.Modules.Microsoft.Roslyn\Workspace\WorkspaceNotReadyException.cs
- src\IntoChat.OS.Modules\Microsoft\Roslyn\src\IntoChat.OS.Modules.Microsoft.Roslyn\Workspace\RoslynNeuron.cs
- src\IntoChat.OS.Modules\Microsoft\Azure\src\IntoChat.OS.Modules.Microsoft.Azure\AzureNeuron.cs
- src\IntoChat.OS.Modules\Microsoft\Aspire\src\IntoChat.OS.Modules.Microsoft.Aspire\Deployment\DeploymentState.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\OperationReceipt.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Integrations\IntegrationState.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Integrations\IntegrationSeeder.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Types\TypeValidationException.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Platform\Secrets\ISecrets.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Types\StringEntry.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Types\SemanticType.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Marketplace\MarketplaceWriteGuard.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Platform\Integrations\IIntegration.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Types\SecretRef.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Secrets\VaultSecretValueSource.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Secrets\SecretState.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\AuthorityNeurons.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Hosting\SiloDiagnostics.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Files\FilesHost.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Platform\Identity\Grant.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Entries\BrainProviderKeys.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Authoring\CreatorModelWorker.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Platform\Entries\IBrainEntries.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Entries\BrainEntriesNeuron.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\AppsModule.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Hosting\IdentityBrainMembership.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\PrincipalCredentialGrain.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Features\FeatureBoot.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Contracts\NeuronType.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Platform\Connections\ConnectionState.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\External\ExternalTicketReplayGrain.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Brain\BrainSceneHost.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Platform\Connections\IConnectionCatalogUpdates.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Connections\Platform\Connections\IConnection.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Authority\AccountGrain.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Apps\IAppMigration.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Authority\BrainAuthorityGrain.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Directory\IdentityStartup.cs
- src\IntoChat.OS.Modules\Microsoft\GitHub\src\IntoChat.OS.Modules.Microsoft.GitHub\GitHubExceptions.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Authority\IdentityActors.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Apps\FastPath\ReactionWriter.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Authority\PrincipalGrain.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Apps\AppState.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Authority\Invitation.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Authority\IdentityStateGrain.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Apps\FastPath\IAppDeploy.cs
- src\IntoChat.OS.Modules\Microsoft\GitHub\src\IntoChat.OS.Modules.Microsoft.GitHub\Repository\GitHubRepository.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Apps\Creator.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\Directory\DeploymentStorageIdentity.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Identity\src\IntoChat.OS.Modules.Identity\Authority\Identity\CallerContextScope.cs
- src\IntoChat.OS.Modules\Microsoft\GitHub\src\IntoChat.OS.Modules.Microsoft.GitHub\Repository\GitHubRepositoryState.cs
- src\IntoChat.OS.Modules\Microsoft\CSharp\src\IntoChat.OS.Modules.Microsoft.CSharp\CSharpModule.cs
- src\IntoChat.OS.Modules\Microsoft\CSharp\src\IntoChat.OS.Modules.Microsoft.CSharp\Runtime\BrainRuntimeState.cs
- src\IntoChat.OS.Modules\Microsoft\CSharp\src\IntoChat.OS.Modules.Microsoft.CSharp\Edge\RuntimeEdge.cs
- src\IntoChat.OS.Modules\Microsoft\CSharp\src\IntoChat.OS.Modules.Microsoft.CSharp\Runtime\Subscription.cs

Module< or new ModuleId(:
- src\IntoChat.OS.AppHost\RunProfile.cs
- src\IntoChat.OS.Modules\Time\src\IntoChat.OS.Modules.Time\TimeModule.cs
- src\IntoChat.OS.Aspire\IntoChat.OS.Aspire.Server\IntoChatOSServerBuilder.cs
- src\IntoChat.OS.Aspire\IntoChat.OS.Aspire.Hosting.Tests.Unit\ModuleOptionValidationFacts.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\Configuration\AIKeys.cs
- src\IntoChat.OS.Modules\AI\src\IntoChat.OS.Modules.AI\AIModule.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Modules\OsModule.cs
- src\IntoChat.OS.Aspire\IntoChat.OS.Aspire.Hosting\Composition\ModuleHosting.cs
- src\IntoChat.OS.Aspire\IntoChat.OS.Aspire.Hosting\Composition\IntoChatOSBuilder.cs
- src\IntoChat.OS\Hosting\IntoChat.OS.Framework\Modules\Configuration\IntoChatOSConfiguration.cs
- src\IntoChat.OS.Modules\Microsoft\GitHub\src\IntoChat.OS.Modules.Microsoft.GitHub\Configuration\GitHubModuleOptions.cs
- src\IntoChat.OS.Modules\Microsoft\CSharp\src\IntoChat.OS.Modules.Microsoft.CSharp\CSharpModule.cs
- src\IntoChat.OS.Modules\Microsoft\CSharp\src\IntoChat.OS.Modules.Microsoft.CSharp\Configuration\CSharpOptions.cs
- src\IntoChat.OS.Modules\Postgres\src\IntoChat.OS.Modules.Postgres\PostgresModule.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\AppsModule.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\FlutterModule.cs

ShippedApps.Is, AppSource.Shipped, CompiledBuiltinCatalog, or ShippedPrograms:
- src\IntoChat.OS.Kernel\IntoChat.OS.Kernel\OsWorkspaceRoot.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Workspace\WorkspaceNeuron.cs
- src\IntoChat.OS.Modules\Google\Flutter\src\IntoChat.OS.Modules.Flutter\Settings\SettingsCaller.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\tests\IntoChat.OS.Modules.Apps.Tests.Unit\AppStateJsonFacts.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Circles\src\IntoChat.OS.Modules.Circles\CircleBirth.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\tests\IntoChat.OS.Modules.Apps.Tests.Integration\FeatureCatalogFacts.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Apps\AppNeuron.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Features\FeatureReconciler.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Features\FeatureCatalog.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Marketplace\AppInstaller.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\src\IntoChat.OS.Modules.Apps\Marketplace\AppStoreNeuron.cs
- src\IntoChat.OS.Modules\IntoChat.OS\Apps\Contracts\IntoChat.OS.Modules.Apps.Contracts\Apps\ShippedApps.cs

Lines the plan expected to be 0, and why they are not:

- `src/IntoChat.OS/Runtime` still exists. Step 1 removed the old neuron/signal files. What remains is the brain contract surface (`IBrain.cs`, `ILogic.cs`, `NeuronProxies.cs`, `BrainSignals/`, `Watch/`, and the other files listed by `Get-ChildItem`).
- `compiled-apps` still exists and contains `os`. Step 3's deletion did not remove that tree. It was not deleted in this step.
- Orleans csproj count is 13. Package references: `IntoChat.OS`, `IntoChat.OS.Framework`, `IntoChat.OS.Client`, `IntoChat.OS.Client.Orleans`, `IntoChat.OS.Aspire.Server`, `IntoChat.OS.Aspire.Client` (the silo-host set), plus the shrinking allow-list `IntoChat.OS.Modules.AI`, `IntoChat.OS.Modules.Flutter`, `IntoChat.OS.Modules.Microsoft.CSharp`, `IntoChat.Cloud.Circle.Server`, plus `IntoChat.OS.Modules.Identity` (unported grains, not on the allow-list). `IntoChat.OS.Kernel.Composition` matches on a project reference, not a package. `IntoChat.OS.Modules.Microsoft.Aspire` matches because it project-references `IntoChat.OS.Modules.Microsoft.Orleans.Contracts`; it has no Orleans package. The allow-list still shrinks to zero in 0.8.0.
- The source pattern count is 127 files, none under a `tests` directory: 40 contain `using Orleans`, 11 contain `IGrainWithStringKey`, 86 contain `GenerateSerializer` (a file can match more than one). `[GenerateSerializer]` was kept on wire records. Identity, Apps, the host, and the client still compile grains. That is the 0.8.0 remainder, not a hidden zero.
- `Module<` / `new ModuleId(` is 16 files. The hits are `new ModuleId(...)` in module option sections and `AddModule<` / `WithModule<` / `AddNeuronModule<` methods. There is no `class Module<TOptions>` declaration in those lines.
- `ShippedApps.Is` / `AppSource.Shipped` is 12 files. They stayed on purpose for the folder scripts. No fact was added about them. `CompiledBuiltinCatalog` and `ShippedPrograms` are in the same pattern and were not separated by the plan's command.

`%LOCALAPPDATA%\DigitalBrain` exists. `%LOCALAPPDATA%\IntoChatOS` does not. The directory was not renamed, moved, or deleted.

Kernel attempts, in order:

1. `ASPNETCORE_ENVIRONMENT=Production`, `--IntoChatOS:Topology=local`, `--urls http://localhost:5000`. The temp Home assignment used `$home`, which PowerShell treats as read-only, so the process was given `IntoChatOS:Home=C:\Users\vhorb` and exited in the guard before it created state. `LegacyConfigurationGuard` threw: `Configuration written for the previous product name is not read (no compatibility layer; see release notes 0.3.0): %LOCALAPPDATA%\DigitalBrain -> %LOCALAPPDATA%\IntoChatOS. Rename each setting, then start again.` Setting Home does not suppress that check. After this attempt, `%USERPROFILE%\state`, `secrets`, `apps`, and `logs` were absent. `%USERPROFILE%\.intochat` was already there (2026-10-25) and was not used.

2. `ASPNETCORE_ENVIRONMENT=Development` (the launch profile's environment; the guard warns instead of throwing), `IntoChatOS:Home=C:\Users\vhorb\AppData\Local\Temp\intochat-cutover-0caaac692794456293b0bd3760a44fd1`, `Topology=local`. The guard let the host through. Startup then threw `OptionsValidationException: Configure IntoChatOS:Auth:Posture (Open or Secured) before starting the host.` The AppHost sets `Open` for a local run. The kernel project has no default.

3. The same Home and topology, plus `--IntoChatOS:Auth:Posture=Open`. The silo refused to start: `OrleansConfigurationException` — `IntoChat.OS.Modules.AI.Inference.InferenceResult` and `InferenceRequest` have no serializer or copier, referenced by `IntoChat.OS.Modules.Apps.ICreatorModel.Generate`. Nothing listened on port 5000. `/health` was not served. The shell checks (Workspace, Desktop, Settings, Files, Calculator, reconnect, Settings modules and connections) and the Brain app check (neurons, synapses, a Calculator particle) were not run, because the process never accepted a connection.

### Step 8 status

Deleted nothing. Ported nothing. The end-state build is 0 errors, 0 warnings. The kernel does not boot: the legacy data directory blocks Production, and in Development the Apps grain `ICreatorModel` still demands Orleans serializers that the cut-over removed. `/health` is curl exit 7. Shell and Brain checks were not observed. Left for 0.8.0: the five-assembly Orleans allow-list, Identity and Apps grains, the silo-host packages, `src/IntoChat.OS/Runtime`, `compiled-apps/os`, and `ShippedApps` / `AppSource.Shipped`.

## Step 8 — Live proof (retry)

Three start failures from the attempt above, plus the container modules that aborted the next start.

- Explicit `IntoChatOS:Home` makes `LegacyConfigurationGuard` look for the old product folder under that Home only. The default Home (Home unset) still checks `%LOCALAPPDATA%`. `%LOCALAPPDATA%\DigitalBrain` was not deleted or renamed. `IntoChatOS:Guard:IgnoreLegacyDataDirectory` still skips the check.
- `LocalProfile.Defaults` sets `IntoChatOS:Auth:Posture=Open` at the lowest precedence. `LocalCloudProfile` shares that dictionary. `CloudProfile.Defaults` stays empty, so cloud still requires Posture.
- `ICreatorModel.Generate` takes and returns `JsonElement` through `RuntimeProtocol.Json`. `Creator` and `ReactionWriter` serialize `InferenceRequest` / `InferenceResult` on that path. No `[GenerateSerializer]` was added to a Contracts project. The silo then reached Active with no further missing-serializer exception.
- A local kernel (and local+cloud, and an unset topology) inserts `IntoChatOS:Modules:<id>:Enabled=false` at the lowest precedence for the modules `RunProfile.LocalDisabledModules` names: postgres, qdrant, clickhouse, supabase, artcraft, playwright, github, azure, roslyn, salesforce, gmail. An explicit `Enabled` still wins. Cloud does not apply them. Without that, `IntegrationClickHouseProvider.Materialize` threw `The ClickHouse integration is unavailable.` during `IntegrationSeeder` and the process exited 1.

`compiled-apps/` was removed from disk (`Test-Path` is false). It stays gitignored. No `.cs`, `.csproj`, `.json`, or `.yml` file references it. The plan, this ledger, and `docs/cutover-assessment.md` still name it.

Command (launch profile `http`: `ASPNETCORE_ENVIRONMENT=Development`, `http://localhost:5310`):

```
dotnet run --project E:\intochat\os-wt\cutover\src\IntoChat.OS.Kernel\IntoChat.OS.Kernel\IntoChat.OS.Kernel.csproj -c Release --no-build -- --IntoChatOS:Topology=local --IntoChatOS:Home=C:\Users\vhorb\AppData\Local\Temp\intochat-cutover-d1b67a0b34c34e56834307823813fe59
```

The log line was `Open posture listening on http://localhost:5310`. Port 5310.

```
curl.exe -fsS -D - http://localhost:5310/health
```

```
HTTP/1.1 200 OK
Content-Type: text/plain
Date: Wed, 28 Oct 2026 20:11:55 GMT
Server: Kestrel
Cache-Control: no-store, no-cache
Expires: Thu, 01 Jan 1970 00:00:00 GMT
Pragma: no-cache
Transfer-Encoding: chunked

Healthy
```

Exit 0. Production was not re-run. The launch profile is Development. A Production kernel with no C# session-token key still fails in `SessionTokens` before it listens.

Shell, with that kernel still up, from the worktree:

```
dotnet run intochat-os.cs -- --IntoChatOS:Topology=local
```

AppHost kernel `OperatingSystem-a` reached `http://localhost:59909/health` with body `Healthy` (Aspire health report `OperatingSystem-a_http_/health_200_check` Healthy). Flutter resource `FlutterShell` ran `flutter run -d windows` and opened a window titled IntoChat (process `intochat_os_flutter`, 1280×720). The widget tree was `BrainSessionGate` → `WorkspaceApp` → `Center` → `Text`.

| Check | Result | Reason |
| --- | --- | --- |
| Workspace renders | not observed | The window text was `No app named workspace exists in this brain.` `POST /brains/owner/apps/connect` returned 409 with that detail. `OsWorkspaceRoot.Connect` requests `apps/workspace` open when desktop is not a shipped app. |
| Desktop renders | not observed | `GET /brains/owner/apps` lists only `brain` and `files`. Desktop is not installed, so connect never takes the desktop view. |
| Settings renders | not observed | Settings is not installed. The shell stayed on the startup error and never opened it. |
| Files renders | not observed | The Files app is Live on that GET (title Files, version 3.0.1), but the shell did not mount it. Connect fails before any app view. |
| Calculator renders | not observed | Calculator is not in the installed list. |
| Brain shows neurons and synapses | not observed | The Brain app is Live (`[App("brain")]`, open operation "Show the brain.") and the shell did not open it. |
| Calculator button produces a particle | not observed | The shell never showed Calculator, so no button was pressed. |

`OsAppsOptions.Apps` is empty (`AddOperatingSystemApps()` with no configure), so `OsDefaults` installs no feature-folder apps. `FeatureReconciler` only updates apps the brain already has. Workspace, Desktop, Settings, and Calculator remain scripts under `features/os/` and were not installed into brain `owner`. FeatureBoot logged `Loaded 18 features from E:\intochat\os-wt\cutover\features`. No Dart file was edited.

`dotnet build Intochat.OS.slnx -c Release`: Build succeeded. 0 Warning(s). 0 Error(s). Time Elapsed 00:00:04.69. `LegacyConfigurationGuardFacts`: 33 passed, 0 failed.

## Step 3 — apps visible again

Unset `AppsOptions.Features` resolves to `features/os`: in Development, walk up from `AppContext.BaseDirectory` to the directory that contains `Intochat.OS.slnx`; when published, `<AppContext.BaseDirectory>/features/os`. The kernel csproj again ships `features/os/**` to the publish output and to NuGet `contentFiles` (the `master` item). A flat folder whose children contain `app.cs` loads as one owner; `features` (owner/app) still loads the same apps. `FeatureCatalog.From` also lists compiled `[App]` types in the Apps assembly whose origin is that assembly. `ShippedInstall` installs every folder script for publisher `os` plus `ShippedApps.Roots` on brain birth, on connect, and from `FeatureReconciler`. A circle brain still gets none of that set. Workspace, Desktop, and Settings stay scripts.

`ShippedScriptCatalogFacts.TheCatalogListsEveryShippedScriptAndFiles` boots the Apps module on TestOS with Features unset and a Development host. The catalog directory ends in `features/os` and lists the 18 script apps plus Files. Calculator's origin is the folder. Files' origin is `FeatureOrigin.Module` for the Apps assembly. `IntoChat.OS.Modules.Apps.Tests.Unit`: 32 passed, 0 failed. `IntoChat.OS.Kernel.Tests.Unit`: 21 passed, 0 failed. `dotnet build Intochat.OS.slnx -c Release`: Build succeeded. 0 Warning(s). 0 Error(s). Time Elapsed 00:00:05.71.

Direct kernel, launch profile `http`, Development, `http://localhost:5310`:

```
dotnet run --project E:\intochat\os-wt\cutover\src\IntoChat.OS.Kernel\IntoChat.OS.Kernel\IntoChat.OS.Kernel.csproj -c Release --no-build -- --IntoChatOS:Topology=local --IntoChatOS:Home=C:\Users\vhorb\AppData\Local\Temp\intochat-cutover-fb39770c7be94d1f9caa37465a8f0157
```

`GET http://localhost:5310/health` returned 200 `Healthy`. The log line was `Open posture listening on http://localhost:5310`. The same process logged `Could not start brain owner's sandbox session` (`Configure csharp Deployment:SandboxUrl or Deployment:SessionPoolEndpoint`). That kernel has no Aspire sandbox, so script apps do not execute there.

Shell, with that kernel still up, from the worktree. Home is a fresh temp directory so the proof does not install into `~/.intochat`:

```
dotnet run intochat-os.cs -- --IntoChatOS:Topology=local --IntoChatOS:Home=C:\Users\vhorb\AppData\Local\Temp\intochat-cutover-shell-0c926fab61114cfa935b4a788394bb56
```

AppHost kernel `OperatingSystem-a` reached `http://localhost:60009/health` with body `Healthy`. The console logged `Loaded 20 features from E:\intochat\os-wt\cutover\features` (18 scripts plus compiled Files and Brain). Flutter resource `FlutterShell` ran `flutter run -d windows` and opened a window titled IntoChat (process `intochat_os_flutter`). `POST /brains/owner/apps/connect` returned 200 with root `view` `owner/apps/desktop/view`. The widget tree was `BrainSessionGate` → `WorkspaceApp` → `NeuronView(owner/apps/desktop/view)`.

`GET /brains/owner/apps` listed 20 Live apps: apps, brain, brainview, calculator, circle, circleroom, datavisualizer, desktop, engineer, files, filmcraft, friends, ino, memory, photocraft, router, selfimprove, settings, usage, workspace.

Visible in that Desktop (view generation 1, and the same widgets on screen):

- Dock: Ino (button text "Ino"), New tab, Apps.
- Right side of the dock: `$0.00`, Me, Settings, Classic layout.
- The window area was empty (`windows` had no children). Ino's window had not opened. `Desktop.FirstRun` is what opens it; this pass did not see that window.

Not dock icons, and why:

- `Desktop.DockOrder` shows Ino, pinned apps, and open apps. Nothing else was pinned or open, so the other installed apps open from the Apps button.
- `Desktop.Hidden` is `ShippedApps.Services`: desktop, workspace, router, selfimprove, circleroom, usage. They are installed and are not windows or dock icons. Usage still shows as the `$0.00` spend text.
- Settings is the More button, not a dock icon and not an open window.
- Files and Brain are in the installed list (compiled origins) and were not open, so they were not dock icons.
- Calculator, Circle, Data Visualizer, Engineer, FilmCraft, Friends, Memory, PhotoCraft, and BrainView are installed the same way and were not open.

08-09 still owes porting Workspace, Desktop, and Settings to compiled `[App]` types. This pass leaves them as scripts under `features/os`. No Dart file was edited.

## Step 8 — Live proof (final)

### cutover(9): shell checklist

An installed app's membership is stamped with the brain owner (`new CapabilityCaller` of `OwnerOfBrain`) before `EnsureMember` and `RemovePrincipalMember`. A cluster listener captures the publishing call's caller and stamps it on the pump, because that pump is not the publishing turn. When the pump still has no caller, `BrainSceneHost` and `FilesHost` stamp the brain owner the same way before they read the graph or open Files. The scene places a directory row (`brain/key`) and the neuron id a synapse stores (`brain/type/key`) as the same sphere. A session neuron is not in the instance directory; the scene still draws it when a synapse names it, so a script subscription has an end.

`dotnet build Intochat.OS.slnx -c Release`: 0 errors, 0 warnings. `IntoChat.OS.Modules.Apps.Tests.Unit` 34 passed. `DigitalBrain.Runtime.Orleans.Tests.Unit` 226 passed.

Production listen, before this shell. Home `C:\Users\vhorb\AppData\Local\Temp\intochat-cutover-prod-9b`, `ASPNETCORE_ENVIRONMENT=Production`, `Topology=local`, `Auth:Posture=Open`, `IntoChatOS:Platform:Storage:ConfiguredCustodyAcknowledged=true`. `GET http://127.0.0.1:5099/health` returned `Healthy`. `<Home>/secrets/session-token.key` is 262 bytes. The same binary without the custody acknowledgement (home `intochat-cutover-prod-9a`) wrote the key and then exited 1 in `BrainStoreCustody` before listen. A direct Production kernel does not see `features/os` (that walk is Development-only), so the shell proof is the AppHost.

Shell, fresh home `C:\Users\vhorb\AppData\Local\Temp\intochat-cutover-shell-9g`:

```
dotnet run intochat-os.cs -c Release -- --IntoChatOS:Topology=local --IntoChatOS:Home=C:\Users\vhorb\AppData\Local\Temp\intochat-cutover-shell-9g
```

The AppHost still starts the kernel from `artifacts/bin/IntoChat.OS.Kernel/debug` (`dotnet run --no-build --configuration Debug`). That Debug kernel was built after the scene change (DLL 23:36:55, process start 23:38:08, pid 76360). `GET http://127.0.0.1:23182/health` returned `Healthy`. Flutter process `intochat_os_flutter` (pid 66424) had window title `IntoChat`. `GET /brains/owner/apps` listed 20 Live apps. `POST /brains/owner/apps/connect` returned 200, root `owner/apps/desktop/view`, `activationApp` `workspace`.

| Check | Result | What was read |
| --- | --- | --- |
| Workspace | observed | Connect names it as the activation app. Workspace revision 5, navigation-error markdown empty. Windows: Ino, Files, Calculator, Brain, Settings. Workspace stays out of the window list (`Desktop.Hidden`). |
| Desktop | observed | View `owner/apps/desktop/view` generation 1 version 5. Texts include Ino, Files, Calculator, and the dock chrome (New tab, Apps). |
| Settings | observed | View `owner/apps/settings/view` generation 1 version 1. Tabs: General, AI models, API keys, Connections, Variables and secrets, Notifications, Archived chats, Danger zone. |
| Files | observed | Empty until the Files app was asked to `open`. `POST /brains/owner/apps/files/request` `{"operation":"open","input":""}` then view `owner/apps/files/full` generation 1 version 1, text "Storages" and "No storage is connected. Add one in Settings." |
| Calculator | observed | View generation 1 version 1, 16 keys. Presses `key-0` action `7` and `key-1` action `8` returned 200 `delivered: true`. Display markdown `78`. |
| Brain neurons and synapses | observed | Empty (revision 0) until `POST /brains/owner/apps/brain/request` open. Then revision 1, 194 spheres, 120 paths. A later workspace `activate` of brain moved the revision from 6 to 17. |
| Calculator particle | observed | While the scene was open, the `8` press was polled. The scene body contained `"type":"particles"` at revision 2. Display was `78`. |

Opening a window (`desktop` request, or workspace `activate`) places the window. Files content and the brain graph are published when that app receives `open`. The first `activate` calls in this process did not fill them: a cluster listener does not replay a signal from before it handled one, and those calls returned before the hosts had opened the apps. The same `open` after the shell was up did. All 20 apps were already Live, so this was not a missing install. No Dart file was edited.

## Operator

- Production file custody still requires TPM or `IntoChatOS:Platform:Storage:ConfiguredCustodyAcknowledged=true`. `LocalProfile` does not default that flag. Without it a Production kernel writes `secrets/session-token.key` and exits before listen.
- `Topology=cloud` still requires csharp `RunTokenKey`. This pass did not start a cloud host.
- Do not merge pull request 335. Do not push a tag and do not run a release workflow.
