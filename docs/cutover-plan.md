# Task: cut IntoChat OS over to digitalbrain, then delete the old half

You are implementing direction B from `E:/projects/agentic_os/agentic-os/clients/intochat/projects/briefs/assessment/2026-10-28-complexity-assessment.md`. Read that file first; every claim below has a file:line there. Read `.planning/workstreams/os-0-6-0/{PROJECT.md,DESIGN-digitalbrain.md,HANDOFF-REMAINING.md}` and `phases/12-*/12-02-PLAN.md` (the deletion ledger) second. Do not re-open owner decisions.

## Repo and rules

- Repo `E:/intochat/os`, branch `master` (local, 11 ahead of origin). Create branch `cutover` from master. Work only there.
- Commit only with `dotnet build Intochat.OS.slnx -c Release` at 0 errors and the touched test projects green. Never force-push, never run `gh release`, `workflow_dispatch` or push tags.
- Clean break for persisted state: no migrations, no dual-read, no feature flags, no `Legacy*` shims. If something must survive, port it; otherwise delete it.
- Owner decisions, fixed: digitalbrain core has zero Orleans types; neurons are plain classes; synapses are durable stream subscriptions; topology is explicit `local | local+cloud | cloud` via `ITopologyProfile`; one repo with `framework/`; product UI is C# value-tree (`DigitalBrain.UI`) only, never hand-written Dart screens; the module is the unit (`[Module]` + static `ModuleManifest`).
- When a test only proves the old plumbing (DI descriptor checks, `*OnlyShrink` ratchets, pin JSON), delete it with the code. Do not rewrite it.
- Every step ends with a commit whose message starts with `cutover(N):`. Keep a ledger at `docs/cutover-ledger.md`: per step, files deleted (count + lines), files ported, tests deleted/ported, build and test command output.

## Target end state (verify with these commands at the end)

```bash
# one vocabulary
! ls src/IntoChat.OS/Neurons src/IntoChat.OS/Signals src/IntoChat.OS/Synapses src/IntoChat.OS/Runtime 2>/dev/null
! ls src/IntoChat.OS.Framework src/IntoChat.OS.Authority src/IntoChat.OS.Connections 2>/dev/null
! ls framework/src/DigitalBrain/Spi/LegacySpi.cs 2>/dev/null
# Orleans only under the runtime and the Orleans module
grep -rl "Microsoft.Orleans" --include=*.csproj src framework | grep -v "DigitalBrain.Runtime.Orleans\|Modules/Microsoft/Orleans\|Tests" | wc -l   # expect 0
grep -rln "using Orleans\|IGrainWithStringKey\|GenerateSerializer" --include=*.cs src | grep -v "/obj/\|Tests\|Modules/Microsoft/Orleans" | wc -l  # expect 0
# one module declaration per module
grep -rl "Module<\|new ModuleId(" --include=*.cs src | grep -v obj | wc -l   # expect 0
# one app path
! ls compiled-apps src/IntoChat.OS.BuiltinApps 2>/dev/null
grep -rn "ShippedApps.Is\|AppSource.Shipped\|CompiledBuiltinCatalog\|ShippedPrograms" --include=*.cs src | grep -v obj | wc -l   # expect 0
# one harness
! ls src/Testing/IntoChat.OS.Testing src/Testing/IntoChat.OS.Testing.Aspire src/Testing/IntoChat.OS.Testing.Neurons src/Testing/IntoChat.OS.Testing.Fakes src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Testing 2>/dev/null
grep -rl "TestBrain.Create\|ReferenceBrain" --include=*.cs src | wc -l    # expect 0
# ratchets gone
ls src/Testing/IntoChat.OS.Architecture.Tests.Unit/*.json | wc -l         # expect <= 2 (ContractManifest only)
# cloud hosted
grep -n "IntoChat.Cloud" src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj   # expect module references
dotnet build Intochat.OS.slnx -c Release
dotnet run --project src/IntoChat.OS.Kernel/IntoChat.OS.Kernel -- --IntoChatOS:Topology=local &  curl -f http://localhost:5000/health
```

## Steps, in order

### 1. One host (`cutover(1)`)
- `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Program.cs` composes only through `AddDigitalBrainRuntime(assemblies)` (`framework/src/DigitalBrain.Runtime.Orleans/Hosting/RuntimeSiloBuilderExtensions.cs:24`) plus the topology profile from `TopologyHosting`.
- Delete `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Composition/Kernel/{Calls,Storage,Authority}/*`, `RetiredReminders.cs`, `OrleansHostWiring.cs` legacy branches; delete `src/IntoChat.OS/Runtime/*` (`PlainNeurons.cs`, `PlainSignals.cs`, `IStillGrains.cs`, `IBrainCalls`).
- Delete `framework/src/DigitalBrain/Spi/LegacySpi.cs`; port its only real users (`HostTimers.cs`, `NeuronHostGrainBase.cs`, `BrainListenerNeuron.cs`, `SessionSynapseNeuron.cs`, `Testing/Fakes.cs`, `TestNeuronHost.cs`) to `ITimers`/`IReminders`/`Neuron.OnDeactivate` in `Spi.cs`.
- Rename `NeedKind.GrainStore` to `NeedKind.State` (core must not carry Orleans words). Update the module template.

### 2. One vocabulary (`cutover(2)`)
- Delete `src/IntoChat.OS/{Neurons,Signals,Synapses,Kernel}/*` and `Contracts/NeutralAttributes.cs`. Anything that still compiles against `IntoChat.OS.Signal`, `IntoChat.OS.NeuronId`, `IntoChat.OS.INeuron` moves to the `DigitalBrain` types. If `src/IntoChat.OS` is then empty, delete the project.
- Delete `src/IntoChat.OS.Framework/*` entirely (Framework, Generators, Contracts.Compute, Contracts.Configuration, all tests). The `IntoChatOSNames` topology strings are replaced by `DigitalBrain.Runtime.Orleans.Topology`. The generator is replaced by nothing: modules are `[Module(id)]` + `static ModuleManifest Manifest` only.
- In every module under `src/IntoChat.OS.Modules/**` and `src/IntoChat.Cloud/**`: delete the old `Module<TOptions>` class, keep the `DigitalBrain.Module` class, move `ConfigureServices` bodies into `ModuleManifest.Settings`/`Needs` or into the runtime where they configure Orleans (e.g. `TimeModule.cs:30` `ReminderOptions`). Options classes stay, bound through `Setting.Of<T>()`.
- Delete `OsCatalog.cs` reflection scan and `OsCatalog.Generated.cs`. The kernel's module list is: every assembly referenced by the Kernel csproj that has a `[Module]` type. Add `IntoChat.Cloud.*` modules to that csproj behind `Topology` gating in their manifest (`LoadsUnder`), not behind a folder glob.
- Remove all Orleans `[GenerateSerializer]`/`[Id]`/`[Alias]` from `*.Contracts` projects; payloads are JSON (`RuntimeProtocol.Json`). Drop the `Microsoft.Orleans*` PackageReference from every Contracts and module csproj.

### 3. One app path (`cutover(3)`)
- An app is a `[App] : App` class (template shape, `framework/templates/.../intochat-app/AppName.cs`) that lives in a module assembly and is hosted in-process by the runtime. Scripts (`features/os/*`) compile in the sandbox against the same `App` base and are loaded by the Apps module from the `~/.intochat/apps` folder only.
- Delete: `src/IntoChat.OS.BuiltinApps`, `compiled-apps/`, `CompiledBuiltinCatalog`, `ShippedPrograms`, `SandboxCompiledApps`, `CompiledAppInstaller`, `CompiledAppPackages`, `ActiveCompiledApps`, `ShippedApps.cs`, the hard-coded lists in `OsApps.cs:46` and `OsWorkspaceRoot.cs:25`, `OsInoMigration.cs`, `FeatureDirectory.IsCompiledSource`, the `features/os/**` copy in `IntoChat.OS.Kernel.csproj:13-15`.
- Port to compiled `[App]` classes in `src/IntoChat.OS.Modules/IntoChat.OS/Apps` (or one module per app): Workspace, Desktop, Settings, Files (`features/os/Files/compiled/Files.cs` becomes the app; delete `app.cs`, `open.cs`, `FilesEntry.cs`, `hosting/`, `IFilesWorkspace`). Keep Calculator, FilmCraft, PhotoCraft as scripts in `features/os/` as the three examples.
- Delete the rest of `features/os/*` (Apps, BrainView, Circle, CircleRoom, DataVisualizer, Engineer, Friends, Ino, Memory, Router, SelfImprove, Usage). Record in the ledger that Ino and Memory return as compiled apps in 0.8.0.
- UI stays as it is (owner decision 2026-10-28): do not delete or rewrite any Dart file, and keep both `IntoChat.OS.Modules.Flutter.Views` and `DigitalBrain.UI` working. Ported apps publish the same value trees they publish today. Consolidating the two C# view vocabularies is 0.8.0.
- BrainView is replaced, not just deleted: add `[App("brain")] BrainScene : App` in the Apps module that drives a Flame scene through `IScene` (`src/IntoChat.OS.Modules/IntoChat.OS/Flame/Contracts/Scene/IScene.cs:7`, schema `FlameSchema.cs:16`): one `sphere` per neuron in the current brain, one `path` per synapse, a `particles` tween along the path when a signal is delivered, camera zoom on tap. It needs a read API over the synapse registry and `ISignalHistory` from the runtime; add it to `DigitalBrain.Runtime.Orleans` as `IBrainObserver` (list neurons, list synapses, subscribe to deliveries) with a `TestOS` implementation. Keep it small; a static graph plus live particles is enough for this pass.

### 4. One identity (`cutover(4)`)
- `framework/src/DigitalBrain.Capabilities.Identity` is the only identity contract. Create `src/IntoChat.OS.Modules/IntoChat.OS/Identity` providing `identity@1` and `secrets@1` on `Neuron<TState>`, ported from `src/IntoChat.OS.Authority/Identity/*`, `Secrets/*`, `Connections/*` (drop Marketplace, Workshop, Compute, Enforcement, pricing, licences; they are 0.8.0 or dead).
- Delete `src/IntoChat.OS.Authority/**`, `src/IntoChat.OS.Connections/**`, `Kernel.Composition/Capabilities/AuthorityVaultPort.cs`, `ConnectionStorage`/`KernelCapabilityConnections` if the broker covers them. Fix the 14 projects that reference Authority (Drive, Gmail, Windows, Flutter, Apps, Mcp, Aspire.Server, Kernel) to use the capability interfaces through `ICapabilityBroker`.
- Collapse the caller records to `CapabilityCaller` (`framework/src/DigitalBrain/Capabilities/CapabilityCaller.cs:12`): delete `KitCaller`, `CallerContext`, `UiEventCaller`, `AuthCaller`, `SessionMember`, both `SessionView`s.
- Cloud.Auth implements `identity@1` sessions through `DigitalBrain.Runtime.Http.ISessionIssuer`. Delete the Authority cookie path. Machine auth (`IntoChatMachine`) stays as the circle's scheme.

### 5. One harness (`cutover(5)`)
- `DigitalBrain.Testing` (`TestOS`, `NeuronHarness`, `SiloHost`, conformance kits) is the only harness. Delete `src/Testing/IntoChat.OS.Testing`, `.Testing.Aspire`, `.Testing.Neurons`, `.Testing.Fakes`, `.Aspire.Testing`, `.Testing.Playwright` (fold the 291 lines into a `DigitalBrain.Testing.Browser` or the one Browser test project), `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Testing`, and their `Tests.Integration` projects.
- Remove the global `Using Include="IntoChat.OS.Testing"` from `Directory.Build.props:86` and the E2E auto-imports.
- For each module test project: tests that exercise behaviour are rewritten on `TestOS`/`SiloHost`; tests that assert DI descriptors, assembly inventories or service-type names are deleted. Target: every module has unit (TestOS), conformance kit subclass per `Provides`, integration (SiloHost). Nothing else.

### 6. Ratchets (`cutover(6)`)
- In `src/Testing/IntoChat.OS.Architecture.Tests.Unit`: delete every `*OnlyShrink` fact, every `Capture*` fact, every pin JSON except `ContractManifest.json`, and `scripts/capture-all.*`, `keys-only-check.ps1`. Keep and fix: `FrameworkReferencesNoIntoChat`, `FrameworkTestsReferenceNoSrc`, `KernelDependenciesPointInward`, `OrleansBan` (now absolute, no allow-list), `NoRetryFacts`, `TestTier*`, `TestShard*`, `CiWorkflow*`, `RepositoryLayout`, `NamespaceScheme`, `Swallow` (no baseline, absolute). Do not add any Dart-related fact.
- `framework/tests/DigitalBrain.Architecture.Tests.Unit` stays as is.

### 7. CI (`cutover(7)`)
- `ci.yml`: build once, then `dotnet test` per tier with `--filter` on the tier trait, not DLL-glob sharding; delete `.github/test-shards.json`, the md5 bucket script, `TestShardFacts`. Keep `framework-*`, `dx04-packages`, `windows-storage`, `gitleaks`, `format`. Make `e2e.yml` a required check for `release.yml` (`needs: ci, e2e-latest`).
- Delete `scripts/verify-single-runtime.py`, `dx04-drive.sh` if CI already does the work.

### 8. Live proof (`cutover(8)`)
- Run the end-state commands above. Start the Kernel with `Topology=local`, open the shell, verify Workspace, Desktop, Settings, Files, Calculator render from value trees, a reconnect replays state, Settings lists modules and connections. Record output in the ledger.
- Also verify the Brain app opens and shows the current brain's neurons and synapses, and that a Calculator button press produces a visible particle.
- Open one PR `cutover -> master` titled `os 0.7.0 cut-over: one host, one vocabulary, one app path, one identity, one harness` with the ledger as body. Stop; the owner merges.

## Reporting

After each step, write the ledger entry and a 5-line status: what was deleted (files/lines), what was ported, what broke and how you fixed it, test results, what you deferred and why. If a step cannot be completed without re-opening an owner decision, stop at that step, state the conflict in one paragraph, and do not work around it with a shim.
