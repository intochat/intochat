# os 0.7.0 complexity assessment (2026-10-28, read-only)

Repo: `E:/intochat/os` master (11 ahead of origin). Planning: `.planning/workstreams/os-0-6-0/`. Phase folders say 91/150 plans done; `STATE.md` and `ROADMAP.md:390` are stale.

## 0. Numbers

| Metric | Value | Source |
|---|---|---|
| csproj | 208 (100 are `*Tests*`) | `find -name '*.csproj'` |
| Product C# lines / test C# lines | 116,465 / 114,440 | `src+framework+features` minus `*Tests*`/`*Testing*` |
| `src/IntoChat.OS.Modules` | 98,658 lines, 50 module dirs, 22 `*.Contracts.csproj`, 15 `*.Aspire.Hosting.csproj` | |
| `framework/src` / `framework/tests` | 22,967 / 17,539 | |
| `features/os` (19 apps) | 12,861 C# lines; 18 of 19 on the old script model, 0 on the framework `[App]` path | agent apps §6 |
| Hand-written Dart product screens | ~28.5k lines, 173 files | `Modules/Google/Flutter/app/{core,shell,ui}/lib` |
| `IntoChat.Cloud/*` | 10,911 lines, referenced by **no host project** | `Intochat.OS.slnx:42-66` only |
| csproj with `Microsoft.Orleans*` PackageReference | 48 (30 outside the two runtimes) | graph script |
| ProjectReference depth (max) | 14 (`Kernel.Tests.*`, `Framework.Tests.E2E`) | graph script |
| Top fan-in | `IntoChat.OS.Testing` 46, `IntoChat.OS.Framework` 40, `Connections` 36, `DigitalBrain.Testing` 25, `IntoChat.OS` 21 | |
| `InternalsVisibleTo` sites | 47 | |
| Architecture facts / pin JSON files / pinned entries | 208 + 26 / 22 / ~2,900 | `src/Testing/IntoChat.OS.Architecture.Tests.Unit`, `framework/tests/DigitalBrain.Architecture.Tests.Unit` |
| `[Fact]/[Theory]` total | 4,486 | |
| `dotnet test` invocations per CI run | 20 (`ci.yml`) + 3 (`e2e.yml`, nightly, gates nothing) | `ci.yml:250,281-296` |
| Topology branches (`Topology.Local/Cloud/LocalCloud`, `IsCloud`) | 25 in `src`, 19 in `framework` | |
| Comments naming a deletion point ("transitional", "until 06-08", "legacy") | Kernel 145, Authority 56, Aspire 30, Framework 20, IntoChat.OS 15 | agent rt §7 |
| TODO/FIXME/HACK/`Skip=` | 0 / 0 / 0 / 0 (banned by `NoRetryFacts`) | debt is encoded as ratchets, not markers |

## 1. Unfinished work (Phases 7–12)

| Phase | Plans | Done | What exists in code | Half-built |
|---|---|---|---|---|
| 7 Files on storage@1 | 10 | 1 (07-02 confirm contract) | `framework/src/DigitalBrain.Capabilities.Storage` 1,456 lines; `IStorage` impls: Drive, Windows, `LegacyStorageAdapter`, `ConnectionStorage` decorator; **no LocalDisk, no Blob** | Files is a hybrid: old shell `features/os/Files/{app,open,FilesEntry}.cs` proxies to `hosting/FilesWorkspaceNeuron.cs:34` which `new`s `compiled/Files.cs:58 [App("files")]`. Compiled into the Apps module by MSBuild link (`Modules.Apps.csproj:9,13,27-31`). |
| 8 app conventions | 12 active (+7 deferred to 0.8.0) | 4 | Analyzers DB0020-28, `AppBaseline`, template `intochat-app` (13 files). | No host loads a template-shaped app (agent apps §4). Four hard-coded install lists still live: `ShippedApps.cs:12,15`, `OsApps.cs:46`, `OsWorkspaceRoot.cs:25`. 08-10..15,18 (Ino 2,608 lines/63 catches, Memory 3,327/75 catches, merges) pushed to 0.8.0. |
| 9 v2.0.1 fold-in | 11 | 2 ticked, 3 more have SUMMARY files not on the roadmap (09-04, 09-05, 09-10) | `IntoChat.Cloud.Auth` 7,802 lines + `Runtime.Http` 182 lines; Mail = contracts + templates, **no `IMailSender` impl**; Organisations = contracts only; Localization = **does not exist** (only an allow-list entry `CloudModuleBoundaryFacts.cs:9,48`); lobby = Dart only (`brain_session_gate.dart:112-155`). | All of it compiles and is tested, none is hosted: kernel discovers modules by glob `IntoChat.OS.Kernel.csproj:35` over `IntoChat.OS.Modules/**`, Cloud lives outside it. |
| 10 circle/cloud | 14 | 4 | `Cloud.Circle.Server` enrolment + machine auth; `OS.Modules.Circles` (2,748 lines) per-brain circle. | `CircleServerModule.cs:62-70` `MapJournal/MapStore/MapFiles/MapApproval` are bodyless partials; `CloudModules.For` (comment `:12`) does not exist. |
| 11 distribution | 9 | 0 | CLI 584 lines. | Whole phase hangs on 08-16. |
| 12 deletion/open source | 11 | 1 (12-10 rotation list) | Deletion ledger 12-02 lists: rest of `IntoChat.OS.Framework` (98 files), `Authority/**` (15k lines, 16 raw `Grain`s), `LegacyGrainStorage`, `LegacySignalJournal`, `IBrainJournalWriter`, os `Signal`, `IRetainsSignals`, `WatchStream`, 26 allow-listed Authority grains, `SynapseShape`, BrainView, `IntoChat.OS.Sdk*`. | Everything on that list is still live and load-bearing. |

Quarantined: 34 `Explicit = true` (17 are pin capture facts); 12 `// Quarantined` sites, reasons only "timing"/"timing under load" (#310-#316, #322, #327). Known flakes: 6 (`HANDOFF-REMAINING.md:50`). Full-solution run times out 7 integration facts.

Scope growth (`ROADMAP.md`): Phase 2 split 7 plans out (l.15,51,63); Phase 3 absorbed them (16 plans); Phase 4 merged with old 6 then grew 15→20 (l.142-145); Phase 6 grew to 18 (l.19,190); Phase 8 added APPS-07 then deferred 7 plans (l.242,256,271); Phase 9 re-targeted twice (l.274,286); Phase 12 re-targeted (l.359-372). Line 5 admits: "18 previews in two days, framework APIs added for os legacy adapters, seven transitional seams".

## 2. The main source of complexity

**digitalbrain was bolted *under* the old OS instead of replacing it, and deletion was scheduled last (Phase 12). So every noun exists twice, every new feature (Files, Auth, Circle) is built against both, and the test apparatus exists mainly to keep the two halves in a legal relation.**

Evidence chain, each a pair that only exists because the cut-over never happened:

```csharp
// NEURON — src/IntoChat.OS/Neurons/INeuron.cs:7          vs  framework/src/DigitalBrain/Neurons/Neuron.cs:9
[Alias("kernel.neuron")] public interface INeuron : IGrainWithStringKey;   // still a grain, "marker" per its own comment
public abstract class Neuron { ... }                                        // plain class

// SIGNAL — src/IntoChat.OS/Signals/Signal.cs:10            vs  framework/src/DigitalBrain/Signals/Signal.cs:4
[GenerateSerializer, Alias("brain.signal")] public record Signal { [Id(0)] NeuronId Publisher; [Id(1)] Trace? Trace; }
public abstract record Signal;

// SYNAPSE — src/IntoChat.OS/Synapses/Synapse.cs:14         vs  framework/src/DigitalBrain/Synapses/Synapse.cs:4
[GenerateSerializer, Alias("synapse")] sealed record Synapse(NeuronId Publisher, SignalId Signal);
sealed record Synapse(NeuronId From, Type Signal, NeuronId To);

// NEURONID — src/IntoChat.OS/Neurons/NeuronId.cs:17        vs  framework/src/DigitalBrain/Identity/NeuronId.cs:10  (both `readonly record struct NeuronId`)

// MODULE — every module is declared twice in ONE file: src/IntoChat.OS.Modules/Time/src/.../TimeModule.cs
12: public sealed class TimeModule() : Module<TimeModuleOptions>(new ModuleId(ModuleName))   // old IntoChat.OS.Framework.Modules.Module
23:   public static DigitalBrain.ModuleManifest Manifest => TimeNeurons.Manifest;
25:   public override Type? Neurons => typeof(TimeNeurons);                                    // bridge
30:   => services.AddOptions<global::Orleans.Hosting.ReminderOptions>()...                     // Orleans in a module
38: [DigitalBrain.Module(TimeModule.ModuleName)]
39: public sealed class TimeNeurons : DigitalBrain.Module                                      // new
// Same shape in Flame: FlameModule.cs:11 (old) + FlameNeurons:30 (new). Discovered twice: OsCatalog.cs:25-54 reflection scan + DigitalBrain Module.Neurons.

// TOPOLOGY — src/IntoChat.OS.Framework/IntoChat.OS.Contracts.Configuration/IntoChatOSNames.cs:23-27 (strings)
//            framework/src/DigitalBrain.Runtime.Orleans/Topology/Topology.cs:22-24 (enum)
//            src/IntoChat.Cloud/.../CircleServerModule.cs:23 ("cloud" a third time)
// Old layer already branches on the new enum: Kernel.Composition/Kernel/OrleansHostWiring.cs:23

// TEST HOST — framework/src/DigitalBrain.Testing/TestOS/TestOS.cs:39  (6,460 lines, used by 36 files under src/)
//             src/Testing/IntoChat.OS.Testing/TestBrain.cs:26,172      (2,348 + Aspire 1,344, used by 242 files)
// Both ship their own fakes, people/identity helpers and composition builder. A third fixture runs both: Testing.Neurons/BothRuntimesFixture.cs:14

// IDENTITY — two full contract sets
// framework/src/DigitalBrain.Capabilities.Identity/Records.cs:27,43,67  Account, Member, BrainEntry
// src/IntoChat.OS.Framework/.../Modules/Identity/{Accounts.cs:4,13, Brains.cs:6}  Account, Member, BrainEntry (+[Id])
// and the same (PrincipalId, AccountId, BrainId) tuple declared 7 times:
record CapabilityCaller(string PrincipalId, string AccountId, string BrainId, string? AppId = null, string? ConversationId = null); // DigitalBrain/Capabilities/CapabilityCaller.cs:12
record KitCaller(...);            // DigitalBrain.Testing/Conformance/CallerScope.cs:9
record CallerContext {...}        // IntoChat.OS.Framework/Modules/Identity/CallerContext.cs:22
record UiEventCaller(...);        // Modules/Google/Flutter/Contracts/.../IUiBinding.cs:26
record AuthCaller(...);           // IntoChat.Cloud/Modules/Auth/.../Services/AuthCaller.cs:8
record SessionMember(...);        // IntoChat.Cloud/.../IAuthSessions.cs:60
record SessionView(...);          // Authority/.../IdentityEndpoints.cs:158  AND Cloud.Auth IAuthSessions.cs:53 (same name, two types)

// SECRETS — two ISecrets implementations bridged by a port
// framework/src/DigitalBrain.Runtime.Orleans/Capabilities/Secrets/SecretsCapability.cs:16
// src/IntoChat.OS.Authority/.../Secrets/SecretsNeuron.cs:10   : Grain, ISecrets        <- bridged via Kernel.Composition/Capabilities/AuthorityVaultPort.cs

// UI — two C# view vocabularies + 28.5k lines of Dart screens
// IntoChat.OS.Modules.Flutter.Views (ViewPublisher/IView/UIVocabulary, used by 18 apps)  vs  DigitalBrain.UI (UiNode/Ui.Reference, used by Files hosting/)
// Apps module, CSharp runtime and Sandbox all reference DigitalBrain.UI (runtime depends on UI)

// APP HOSTING — 5 paths, 6 catalogs (agent apps §1)
// A script app: features/os/<App>/open.cs -> FeatureLoader.cs:21 -> CSharpAppRuntime.cs:10 -> sandbox Program.cs
// B shipped package: compiled-apps/os/*.intochat-app -> CompiledBuiltinCatalog.cs:8 -> ShippedPrograms.cs:16 -> same sandbox
// C uploaded package: SandboxCompiledApps.cs:25 / CompiledAppInstaller
// D brain-authored source: AppNeuron state -> path A
// E in-process DigitalBrain neuron: features/os/Files/hosting/FilesWorkspaceNeuron.cs:34  (Files only, via MSBuild link)
// F template [App] + Neuron<T>: framework/templates/.../intochat-app — NO host loads it
```

The plumbing that holds the two halves together is itself the largest growth area:

```text
src/Testing/IntoChat.OS.Architecture.Tests.Unit   208 facts, 22 pin files, ~2,900 pinned entries
  NeuronGrainTypes.json ~756   ContractManifest.json ~649   ExportedStateBaseline.json ~455   OrleansAttributeOwners.json ~411
  OrleansReferenceOwners.json ~99   NeuronsStillGrains.json ~26   GrainInterfaceOwners.json ~27   ObserverPathOwners.json ~21 ...
  *OnlyShrink facts: OrleansAttributes, GrainInterfaces, NeuronsStillGrains, GrainFactoryUse, OrleansPackageReferences, ObserverPath, LegacyHubSignals, ModuleIdsInApps, FrameworkVendorReach
src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Composition (2,805 lines) — csproj:4 "transitional legacy adapters between the os Framework and DigitalBrain.Runtime.Orleans"
  Calls/PlainNeuronCallFilter.cs:12, RuntimeCallerMirror.cs:8, PlainInstanceRegistration.cs:14, Storage/LegacyGrainStorage.cs:9, LegacySignalJournal.cs, RetiredReminders.cs:6
src/IntoChat.OS/Runtime/{PlainNeurons.cs:6, PlainSignals.cs:6, IStillGrains.cs:3}  — all say "deleted with the Framework in 06-08"; 06-08 is marked done, they are still here
framework/src/DigitalBrain/Spi/LegacySpi.cs — INeuronTimers, IRemindedNeuron, IDeactivatingNeuron, IRuntimeEndpoint: a second SPI inside the *new* core, added for os adapters (ROADMAP:5,142)
framework/src/DigitalBrain/Modules/Need.cs:7  enum NeedKind { GrainStore, ... }  — Orleans vocabulary in the Orleans-free core
```

Why this is the root and not a symptom: every secondary problem below is a *pair*. Remove the old half and the pair, its bridge, its ratchet and its pin file vanish. 44 of the 59 remaining plans are "build X on the new model" or "delete the old X"; the ones that keep slipping (08-10..18, 09-11, 12-*) are exactly the deletions.

## 3. Secondary problems (ranked by cost)

1. **App model: 5 hosting paths, 6 catalogs, 0 apps on the path the framework advertises.** `features/os/*` = 12,861 lines all on `IntoChatOSClient` + string keys (`app + "/display"`), packed into 19 committed `.intochat-app` zips by `BuiltinPacker.cs`, run in a sandbox. ~20 `ShippedApps.Is(` / `AppSource.Shipped` branches (`ShippedPrograms.cs:20, FeatureLoader.cs:16, AppInstaller.cs:191,232,240, AppStoreNeuron.cs:43,94, OsInoMigration.cs:18-102`). 75 hard-coded app-name literals in runtime (`ino` 16, `apps` 12, `desktop` 10). Plus 28.5k lines of hand-written Dart product screens (`workspace_app.dart` 612, `compute_usage_panel.dart` 581, `connect_window.dart` 481, `login_screen.dart` 316, chat/* ~1k) which the owner rule says must not exist.
2. **Authority + Connections (15,084 + 2,678 lines) are the real identity/secrets/storage plumbing and have no framework counterpart started.** 16 classes derive `Grain` directly (`Authority/Secrets/SecretsNeuron.cs:10`, `Compute/BrainComputeGrain.cs:15`); `Connections/Platform/Integrations/IIntegration.cs:19 : IGrainWithStringKey`, same in `ISecrets.cs:9`, `IBrainEntries.cs:33`, `ICircleShares.cs:11`. 5 authentication paths (Authority cookie, Cloud.Auth session, `IntoChatMachine` scheme, Google OAuth, client `SessionStart` token at `Client/Runtime/V1/RuntimeProtocol.cs:76`). 14 projects reference Authority, including Drive, Gmail, Windows, Flutter, Apps.
3. **Test apparatus ≈ product.** 114k test lines vs 116k product; 100 test projects; two harness stacks; `IntoChat.OS.Testing` is a global using in every test project (`Directory.Build.props:86`) and has fan-in 46. 208 architecture facts prove structure, not behaviour; sampled Kernel/Framework unit facts are DI-descriptor checks (`Kernel.Tests.Unit/OsStorageFacts.cs:10-17`, `TopologyCompositionFacts.cs:30,68`, `CompositionFacts.cs:73`, `ModulePriceListFacts.cs:86,97`). 20 `dotnet test` invocations per CI run, no `--filter`, nightly e2e gates nothing, `Aspire.Hosting.Tests.E2E` and `CSharp.Tests.E2E` run nowhere.
4. **Cloud is dead code.** `IntoChat.Cloud/*` 10,911 lines, 5 test projects, zero host references; Organisations and Mail have no implementation; Localization is a name in an allow-list; `CircleServerModule` routes are empty partials. Phase 9 "partially merged" means copied in, compiled, and not run.
5. **Module ceremony.** 50 module dirs → 22 Contracts + 15 Aspire.Hosting + tests = 208 csproj, depth 14. A hosted module touches ~11 points (agent rt §5: 2 csproj, two `Module` classes, options, slnx, `OsCatalog.cs:20-21` exclusions, `RunProfile.cs:36-46,92-93,152-171`, 3 pin JSONs, consumer csprojs, optional generator opt-in). The `Framework.Generators` emitter is adopted by 4 of ~30 modules and hand-duplicated by the rest (`TimeModule.cs:12-16`).

## 4. Directions for the root cause

### A. Finish the roadmap as ordered (7 → 8 → 9 → 10 → 11 → 12)
Build Files, Auth, Circle, CLI on both models, delete the old one at the end.
- Cost: every feature pays the bridge tax now (Files already did: 8 files, 3 MSBuild edits, 2 view vocabularies). Phase 12's ledger grows with each phase. 59 plans, history says the count rises.
- Benefit: product never goes dark; ratchets keep it honest.
- Verdict: this is the path that produced the current state.

### B. Cut over first, then build (invert 12 → 7) — **recommended**
Make the deletion ledger (12-02/04/05/06) the next phase, as one vertical slice:
```text
1. One host: Kernel Program.cs -> AddDigitalBrainRuntime(modules) only. Delete IntoChat.OS.Kernel.Composition/Kernel/{Calls,Storage,Authority}, IntoChat.OS/Runtime/*, LegacySpi.cs.
2. One vocabulary: delete src/IntoChat.OS/{Neurons,Signals,Synapses,Kernel}/* ; modules keep only the [DigitalBrain.Module] class; delete IntoChat.OS.Framework (+Generators, Contracts.*).
3. One app path: a module can ship [App] classes (template shape) loaded in-process; scripts stay sandboxed but compile against the same App base. Delete paths B/C/D catalogs, BuiltinPacker, the 19 committed zips, ShippedApps/OsApps/OsWorkspaceRoot lists (08-04 becomes "the catalogue is the module list").
4. One identity: Capabilities.Identity is the contract; Authority Identity/Secrets/Storage grains become one IntoChat.OS.Modules.Identity module on Neuron<T>; delete Authority + Connections grain contracts. Cloud.Auth plugs into identity@1 (it already targets Runtime.Http).
5. One harness: TestOS + SiloHost; delete IntoChat.OS.Testing/.Aspire/.Neurons/.Fakes, Kernel.Testing; rewrite the ~242 TestBrain call sites per module as the module is ported (or delete the test with the old code).
6. Delete every *OnlyShrink fact and its pin; keep ~10 boundary facts (framework references no src, Orleans only under runtime, no Dart product screens).
7. Rehost apps: Workspace/Desktop/Settings/Files as compiled [App]; Calculator/FilmCraft/PhotoCraft as scripts; Ino/Memory/BrainView/Friends/CircleRoom/SelfImprove/Router/Usage/DataVisualizer/Apps deleted or parked unhosted until 0.8.0 (owner already accepted clean break and deferred their rewrites).
```
- Cost: the shell is dark for Ino/Memory until rewritten; Dart screens must be replaced by value-tree screens (owner rule) or the shell keeps a chrome-only role; the Windows kit has no CI leg; ~2–3 weeks of pure deletion with no feature.
- Benefit: Phases 7, 9, 10 are then built once. Estimated plan impact below.
- Risk to manage: do it on the `os-0-7-0` branch as one PR series with the build green at every step, not as a flag.

### C. Shrink the ambition: drop "third-party framework" for 0.7.0
Keep digitalbrain internal, delete DX-04, `Packable_ids_*`, template smoke, conformance kits, the framework Architecture project, the NuGet story; merge `framework/` into `src/` as ordinary projects.
- Cost: gives up the stated Core Value ("build an app without knowing Orleans exists") and the open-source positioning.
- Benefit: removes ~25 facts and 3 CI jobs. Does not remove any pair above; the duplication stays.
- Verdict: cheaper than A, does not address the root.

### What B does to Phases 7–12

| Plan | Under B |
|---|---|
| 07-01, 07-03, 07-05, 07-10 | unchanged (LocalDisk, Blob, confinement, access log) |
| 07-04 | unchanged, still blocked on Google scope |
| 07-07 | cheaper: one upload endpoint on Runtime.Http, no call-filter bridge |
| 07-08 | cheaper: `compiled/Files.cs` becomes the app; `hosting/`, `app.cs`, `open.cs`, `FilesEntry.cs`, `IFilesWorkspace` disappear |
| 07-09 | unnecessary (script Files, `RequestLedger`, old `IStorage` already deleted in the cut-over) |
| 08-04, 08-06, 08-07 | unnecessary or trivial (one list = module manifests; analyzers already run on compiled apps) |
| 08-08, 08-09 | unchanged in intent, cheaper (no packer, no sandbox-vs-builtin split) |
| 08-16, 08-17, Phase 11 | unchanged; CLI renders from `LocalProfile.Services` |
| 09-04..09-09 | built once on identity@1; `IntoChat.Cloud.*` moves under the kernel glob or the glob is replaced by the manifest list; Mail/Org/Localization need real implementations either way |
| 09-10, 09-11 | unchanged (deploy, operator) |
| 10-06..10-15 | unchanged; `CloudModules.For` adapter unnecessary |
| 12-02, 12-04, 12-05, 12-06 | pulled to the front (they *are* B) |
| 12-01, 12-07, 12-09, 12-11, 12-12, 12-13 | unchanged (scan, CI, docs, archive, public flip, nuget) |

Net: ~10 plans drop, ~8 get cheaper, the rest unchanged. The deletion work is the same size as today's ledger; it just stops growing.

## 5. Side note: BrainView and a Flame "live brain" scene

08-13 deletes `features/os/BrainView` (4 files, 142 lines). The right replacement is not a Dart screen but a Flame scene driven by a neuron, which fits the owner rule (a scene is data):

```csharp
// exists today: src/IntoChat.OS.Modules/IntoChat.OS/Flame
//   Contracts/Scene/IScene.cs:7  IScene            Contracts/FlameSchema.cs:16  node types incl. "sphere"(icon,title,neuron,focused,...), "particles", "path", tween/tree vocab
//   src/Scene/SceneNeuron.cs:9   already a DigitalBrain [Neuron] (FlameModule.cs:27-30)
// needs from the runtime (after cut-over): a read API over the synapse registry + ISignalHistory
[App("brain", "1.0.0", Title = "Brain")]
public sealed class BrainScene : App
{
    [On] Task Open(Opened _)             => Scene.Set(Layout(Context.Synapses.All()));          // spheres = neurons, paths = synapses
    [On] Task Fired(SignalDelivered s)   => Scene.Tween(s.Synapse, "particles", from: s.From, to: s.To);  // a signal flying along its synapse
    [On] Task Focus(SphereTapped t)      => Scene.Camera(t.Neuron, zoom: 2);                      // zoom to a neuron, show its state side panel
}
```
This is a good first showcase app for the framework because it exercises exactly the two things 0.7.0 promises (synapses as first-class, signals observable) and nothing else. Park it in 0.8.0 after B; before B it would need the old `WatchStream`/observer path, which is on the deletion list.

## 6. Threats to this reading

- Verified: master `a9c5fd9e1` (2026-10-28) contains `4152473e1` (the `os-0-7-0` HEAD from the handoff) and the `phase9/cloud-move` merge, so these numbers are the post-Phase-6 state. The 06-08 commits (`267da449e`, `c47a71f5e`) removed the observer path and `Runtime.InMemory` but left `PlainNeurons.cs`, `IStillGrains.cs`, `LegacyGrainStorage.cs`, `LegacySpi.cs`, `IntoChat.OS.Framework` and Authority in place, each still annotated "deleted in 06-08".
- Line counts include generated `*.g.cs` only where committed; Dart counts exclude tests.
- I did not run the suites; flake and timeout figures are from `HANDOFF-REMAINING.md:50`.
