# The Kernel — design and migration plan

**Date:** 2026-10-09 · **Status:** ratified in conversation (grilling rounds Q1–Q9), not started
**Branch base:** `os-runtime`

## 1. Diagnosis — the main source of complexity

DigitalBrain has no kernel. The OS's own machinery (secrets, identity, connections, indexes, inbound
pipes, feature loading) is built *out of neurons*, then hidden again. Every hiding mechanism is its own
feature, and each one looked like an independent problem:

| Symptom | Why it exists |
|---|---|
| `[PlatformOnly]` (13 interfaces) | kernel state and inbound plumbing sit on the public neuron bus and must be hidden |
| Three framework rings + architecture tests | hide platform types from scripts at compile time |
| `ScriptContracts`, `ContractCatalog` filtering, `ContractCatalog.Deployed()` | hide types from the sandbox and MCP — computed 3+ ways |
| `NeuronAccess Access(operation)` | a second access model beside type visibility |
| ~16 registries (`IAppIndex`, `IConnectionIndex`, `IToolSources`, `ICSharpCatalog`, `ITableIndex`, Gmail/GitHub indexes, `IRevisions`+launcher, `IMarketplace`+`ShippedCatalog`, …) | nobody owns "what exists", so each domain reinvents it |
| ~6 type catalogs (`OsCatalog`, `ModuleInventory`, `ContractCatalog`, `ScriptContracts`, `CSharpContractDiscovery`, `CapabilityCatalog`, connector copies) | nobody owns "what types exist" |
| Ad-hoc keys (`{scope}/apps/{name}`, `connection/{brain}/{connector}/{id}`, `{brainId}/tools`, …) | no kernel naming rule |
| Many app formats (app.json, spec.md, synapses, lib, shipped dirs, publish-at-startup) | nobody owns "how a feature is loaded" |

**Access is decided by whether you can *see a type*, not by *who is calling*.** We already have the right
primitive — the kernel-stamped caller — but security is implemented as visibility.

**The fix:** a real kernel — a small owner of facts that is *not* a neuron. Above it, every neuron is
callable; access is decided only by the stamped caller.

> If it is a fact *about the OS*, the kernel owns it. If it is a capability, it is a neuron.

## 2. Decisions (ratified)

| # | Decision | Answer |
|---|---|---|
| Q1 | What is an instance | A neuron with persisted state. Activations are caches; `StatelessWorker`s, routers, model catalogs are not instances. |
| Q2 | Keys | Uniform `{brain}/{type}/{id}`; `{type}` is the contract alias. Per-brain singletons use id `_`. Brain, type and partition are all parsed from the key. |
| Q3 | Type dictionary scope | Compiled types: static layer from `ModuleInventory` (process-wide). Feature types: per-brain table. `kernel.Types()` returns the union; nothing copied. MCP asks the kernel — no DLL scan. |
| Q4 | Registration | **Existence:** automatic, kernel-stamped, on first persisted write (in the `Neuron` base write path). **Liveness:** `IManagementGrain` query (cluster-wide) filtered by `{brain}/` prefix + membership check; on demand only, never polled; no state of our own. **Activity:** OpenTelemetry, unchanged. No `Activated`/`Deactivated` signals. |
| Q5 | Domain indexes | Deleted. No wrappers. Callers use `kernel.Instances<T>()`. |
| Q6 | Instance entry | `Key`, `Created`, `Creator` (stamped caller). Nothing else — titles/descriptions stay in the neuron. |
| Q7 | Kernel owns | instances, types, identity (callers, brains, membership), secrets/credentials, connections, feature loading, access decisions, signal stamping. Nothing else. |
| Q8 | How kernel is reached | `IKernel` service, not a neuron. Neurons get it injected; scripts get a read-only slice `brain.Kernel` (`Instances<T>()`, `Types()`, `Running()`) + kernel signals. |
| Q9 | Access | One check per call: caller × instance × operation, made by the kernel. Contract methods declare `[Owner]` / `[Member]` / `[Public]` (default `[Owner]`). `NeuronAccess` and `[PlatformOnly]` deleted. |

Related, earlier in the same conversation:
- **Infrastructure is not a feature.** AppHost/modules stay compiled and declarative (operator's world).
  `dotnet run digitalbrain.cs` starts the kernel; it is not a synapse.
- **Features are the one user format:** one `.cs` file per feature; manifest + spec folded into it.
  Disk is just an install source. Done *after* the kernel lands (Phase 6).
- **Open fork, deliberately deferred:** script-defined neurons (would let modules shrink to connector code
  + contracts). The type table's feature layer is designed so this can land later without reshaping.

### 2.1 Phase 0 findings (2026-10-09) — amendments

- **Q2 simplified:** an Orleans `GrainId` already carries the grain type, so keys do **not** need a type
  segment. Rule: every brain-owned key is `{brain}` or starts with `{brain}/`. The kernel takes the brain
  from the key and the type from `GrainId.Type`. Bare-brain singletons (`IAppIndex`, `IWorkspace`, …) are
  already valid. Off-pattern keys to fix in Phase 1: `connection/{brain}/{connector}/{id}` →
  `{brain}/connection/{connector}/{id}`; `ITableIndex` JSON key; `IComputeRecords` `usage/{hash}`;
  `IShellState` `shell-{digest}`/`IShellPart`; `IIntentUsage`; script-built connection keys in
  `DataVisualizer/synapses/refresh.cs:76`, `requests.cs:99`. Platform-wide keys (marketplace, identity,
  secrets, integrations, ledger, mailbox/repo indexes) are kernel-internal or deleted later — untouched in Phase 1.
- **Q4 hook:** there is no single write funnel (~30 grains call `WriteStateAsync` directly; a second base
  `IdentityStateGrain`). Registration hooks a **decorator around the keyed `IGrainStorage` "Default"**:
  `WriteStateAsync(stateName, grainId, state)` with `!state.RecordExists` = first write. Caller from
  `CallerContextStamper.TryGet` (may be absent for reminders/signal delivery → creator = null/system).
  Only grain classes deriving `Neuron` register; kernel/index grains excluded by being deleted or made
  non-`Neuron`. Registration idempotent per grainId (multi-state grains write first-time per stateName).
- **Liveness:** Orleans 10.3.1 `IManagementGrain.GetActiveGrains(GrainType)` → `List<GrainId>`, filter
  `id.Key` by `{brain}/` prefix. Diagnostic broadcast, on demand only.
- **Existing `KernelNeuron`** (`Framework/Runtime/Brain/KernelNeuron.cs`, key `{brain}`, implements `IBrain`)
  — Phase 2 must reconcile: the brain neuron stays a neuron; kernel *state* goes into non-`INeuron` grains.
- **Access today:** `NeuronCallFilter` (`Framework/Callers/NeuronCallFilter.cs`) + 12 `Access` overrides +
  `NeuronAccess.ForBrainKey`; caller stamped in `RequestContext["digitalbrain.caller-context"]`.

### 2.2 Implemented status (2026-10-10)

Phases 1�5 are done on `os-runtime`; Phase 6 (features load through the kernel) is pending design.
Where the code deviates from this spec:

- **Key grammar:** only `/` separates a brain from the rest of a key. UI parts are `{brain}/ui/{name}`
  (were `{brain}:{name}`); `BrainScope.IsValidId` refuses `:` and a leading `@`.
- **Registration:** only `{brain}/...` keys register, registered before the first write lands (retired
  again if the write fails). Bare keys (the brain itself, account-keyed neurons, `marketplace`) and
  `@platform/...` keys (e.g. `@platform/meters/{period}`) never register. A neuron keeps one persisted
  record (multi-state grains folded into the primary state). `TryAddKernelRegistration` false fails startup.
- **Kernel surface:** `IKernel.InstancesIn(brain, �)` for platform code with no caller; `IAppIndex` is
  gone, scripts read apps through `brain.Kernel`.
- **Access:** declared on contracts as `[Public]`, `[Shared]`, `[Internal]`, `[PerAccount]` (else the
  owner rule), enforced once by `NeuronCallFilter`, on `INeuron` contracts only.
- **MCP** still loads contract DLLs to make calls; it reads types from the kernel.

## 3. Target model

### 3.1 Kernel surface

```csharp
// DigitalBrain ring (script-visible, read-only slice)
public interface IKernelView
{
    IAsyncEnumerable<Instance> Instances<T>(CancellationToken ct = default) where T : INeuron;
    IAsyncEnumerable<Instance> Instances(string type, CancellationToken ct = default);
    Task<TypeEntry[]> Types(CancellationToken ct = default);
    Task<Instance[]> Running(CancellationToken ct = default);   // IManagementGrain, filtered
}

public sealed record Instance(string Key, DateTimeOffset Created, Caller Creator);
public sealed record TypeEntry(string Name, string Alias, TypeKind Kind, string Provider, string Shape);
public enum TypeKind { Neuron, Signal, Connector }

// Kernel signals, published by the kernel itself
public sealed record InstanceCreated(string Key) : Signal;
public sealed record InstanceRetired(string Key) : Signal;
public sealed record TypeAdded(string Name) : Signal;

// Framework ring (neurons, module authors)
public interface IKernel : IKernelView
{
    Task Retire(string key);                       // clears state, drops entry, publishes InstanceRetired
    ICallerAccess Access { get; }                  // caller × instance × operation
    IIdentity Identity { get; }                    // callers, brains, membership
    ISecretStore Secrets { get; }                  // sealed, per connection
    IConnections Connections { get; }              // connection records are instances, credentials are kernel
    IFeatureLoader Features { get; }               // Phase 6
}
```

Exact names are placeholders; shapes are what was ratified.

### 3.2 Kernel storage

| Fact | Storage | Key |
|---|---|---|
| Instances | grain state, one grain per (brain, type) — partitioned so no hot grain | internal `kernel:{brain}:{type}` |
| Feature types | grain state per brain | internal `kernel:{brain}:types` |
| Compiled types | static, from `ModuleInventory` at boot | — |
| Identity / brains / membership | grain state (platform-wide) | internal |
| Secrets | sealed grain state per connection | internal |

Kernel grains are implementation details of `IKernel`: they **do not implement `INeuron`**, are not in the
type table, and cannot be addressed through `brain.Get<T>`, MCP or scripts. That — not an attribute — is
what makes them private.

### 3.3 Registration path

1. `Neuron` base wraps the persisted-state write (the one place every write goes through).
2. On the first successful write for a key (state did not previously exist — `RecordExists` false),
   the base calls `kernel.Register(key, stampedCaller)` (internal).
3. Kernel parses `{brain}/{type}/{id}`, appends to the (brain, type) partition, publishes `InstanceCreated`.
4. Idempotent: re-registering an existing key is a no-op (covers retries and at-least-once).
5. `Retire(key)` clears neuron state, removes the entry, publishes `InstanceRetired`.

No module code calls registration. No module can forge an entry.

### 3.4 Access path

- Every neuron call already carries a kernel-stamped caller. The call filter asks
  `kernel.Access.Allows(caller, key, operation)` once.
- Policy from the contract method attribute: `[Owner]` (brain owner/members acting as the brain — default),
  `[Member]`, `[Public]`.
- Connector inbound: the connector writes into its edge neuron **as itself** (stamped connector caller).
  The edge neuron's inbound methods are `[Owner]` where the connector is the owner of its edge neurons.
  `IGmailInbound`, `IGitHubInbound`, `IConnectionInbound`, `IWebBrowserConnector`, `IAspireResourceReporter`
  fold into their neuron contracts or into the kernel connection path.

### 3.5 What MCP, the shell, the Creator and Jev read

| Consumer | Today | After |
|---|---|---|
| `neurons_list` | contract types via reflection | `kernel.Instances(...)` — real instances, optionally by type |
| `neurons_search` | `CapabilityCatalog` (contracts + tool snapshot) | `kernel.Types()` + tool neurons' live `Tools()` |
| MCP contract set | `ContractCatalog.Deployed()` DLL scan | `kernel.Types()` over the wire |
| Shell app list | `IAppIndex` | `kernel.Instances<IApp>()` + `On<InstanceCreated>` |
| Creator contract discovery | `CSharpContractDiscovery` | `kernel.Types()` grouped by `Provider` |
| Router menu | `CapabilityCatalog` | `kernel.Types()` ∩ caller-allowed + installed app operations |
| "What's running" | nothing (traces only) | `kernel.Running()` |

## 4. Deletion list (the point of the exercise)

Every item is verified with LSP `findReferences` before deletion (CLAUDE.md, memory: lsp-references-cross-check).

**Attributes / access**
- `PlatformOnlyAttribute` and all 13 usages
- `NeuronAccess`, `Neuron.Access(string operation)` overrides
- `[PlatformAssembly]` and the architecture-test rules that only exist to enforce it (re-evaluate Platform ring; see §7)

**Type catalogs**
- `ContractCatalog.Deployed()` (DLL scan)
- `ScriptContracts`' private neuron dictionary → reads `kernel.Types()`
- `CSharpContractDiscovery` / `ScriptContractCatalog` → query over `kernel.Types()`
- `CapabilityCatalog`, `IRuntimeCapabilitySource`, `ToolCapabilitySource`
- `ConnectorCatalog`'s copied neuron/signal names (connector entries become `TypeKind.Connector`)
- `ContractCatalog` itself shrinks to the compiled-layer builder inside the kernel

**Instance indexes**
- `IAppIndex` / `AppIndexNeuron`
- `IConnectionIndex` / `ConnectionIndexNeuron`, `IConnectionCatalogUpdates`
- `IToolSources` / `ToolSourcesNeuron` (tool neurons are instances of tool-source types; live `Tools()` only)
- `ICSharpCatalog` / `CSharpCatalogNeuron` (name/purpose move into the C# file neuron's state)
- `ITableIndex`, `ITableLifetime`, `ITableDescriptor` (tables are instances; descriptor is table state)
- `GmailMailboxIndex`, `GitHubRepositoryIndex`, `TimeInventory`
- `IRevisions` duplication with the launcher → one owner (§7 Q-open)
- Per-module `static string Key(string scope)` helpers; `BrainScope` key parsing becomes kernel key parsing

**Kernel-state neurons → kernel internals**
- `ISecrets`, `IIdentityDirectory`, `IIntegration` (stop being `INeuron`)

## 5. Phases

Each phase ends with the CLAUDE.md verification loop: LSP clean → build affected projects → `aspire run`
→ resources Healthy → drive through `digitalbrain-mcp` → confirm spans/logs in Aspire telemetry → unit + E2E
suites of every touched project (5-minute cap per run). No state compatibility: wipe local storage when
grain state reshapes; no migrations.

### Phase 0 — Facts before code (no edits)
- Verify `IManagementGrain` API on current Orleans (context7 / microsoft-learn): method names, cost, key
  visibility for string-keyed grains.
- LSP map of the `Neuron` base persisted-write path; confirm there is exactly one write funnel.
- LSP map of every grain key format and its constructors (the input to Phase 1).
- LSP `findReferences` for each item in §4; record callers per item in this doc.

### Phase 1 — Uniform keys
- Introduce `NeuronKey` (`{brain}/{type}/{id}`, parse/format, `_` for singletons) in the DigitalBrain ring.
- Move every neuron key constructor to `NeuronKey`. `connection/{brain}/…` becomes `{brain}/connection/{id}`.
- `BrainScope.CurrentId()` and `BrainRoutes` read brain from `NeuronKey`.
- Shell (Dart) key construction updated in the same change.
- **Done when:** app runs, `neurons_get` works on an app, a connection and a C# file with new keys.

### Phase 2 — Kernel skeleton + instance directory
- `IKernelView` / `IKernel`, kernel grains (non-`INeuron`), partition per (brain, type).
- Registration in the `Neuron` write funnel; `InstanceCreated`/`InstanceRetired`; `Retire`.
- `kernel.Running()` via filtered `IManagementGrain`.
- MCP: `neurons_list` → instances.
- Tests (`DigitalBrain.Testing.Module`): first write registers once; second write no-op; stateless worker
  never registers; retire removes + signals; brain A cannot list brain B; forged registration impossible
  (no public register API).
- **Done when:** creating an app via MCP shows it in `neurons_list` and an `InstanceCreated` span appears.

### Phase 3 — Collapse instance indexes
Order (smallest blast radius first, each its own commit):
1. `ICSharpCatalog` → C# file neuron state + `Instances<ICSharpFile>()`
2. `IAppIndex` → `Instances<IApp>()`; shell subscribes `On<InstanceCreated>`
3. `IToolSources` → `Instances` of tool-source types + live `Tools()`
4. `ITableIndex`/`ITableLifetime`/`ITableDescriptor`
5. Gmail/GitHub/Time indexes
6. `IConnectionIndex` (after Phase 5 moves connections under the kernel — or here if trivial)

### Phase 4 — Type dictionary
- Compiled layer built once from `ModuleInventory`; feature layer per brain (empty until Phase 6).
- `kernel.Types()`; `TypeAdded` signal.
- Replace `ContractCatalog.Deployed()`, `ScriptContracts` dictionary, `CSharpContractDiscovery`,
  `CapabilityCatalog`, `ConnectorCatalog` name copies, router `Menu` source, `NeuronTypeSearch` input.
- digitalbrain-mcp gets types from the silo, not from DLLs beside it.
- **Done when:** `neurons_search "timer"` returns `ITimer` with MCP built without module Contracts DLLs.

### Phase 5 — Access + kernel-owned identity/secrets/connections; delete `[PlatformOnly]`
- `[Owner]/[Member]/[Public]` on contract methods; one kernel access check in the call filter.
- `ISecrets`, `IIdentityDirectory`, `IIntegration` become kernel internals (non-`INeuron`).
- Inbound interfaces fold into connector-stamped writes.
- Delete `PlatformOnlyAttribute`, `NeuronAccess`, ScriptContracts' visibility filter.
- Architecture tests: replace "platform types invisible" with "no kernel type implements `INeuron`" and
  "every contract method resolves to an access policy".
- **Done when:** a script calling a connection's credential path fails at compile (type not in sandbox
  because it's not a neuron) and a cross-brain call fails at the kernel access check — both seen in logs.

### Phase 6 — Features load through the kernel (the original ask)
- `IFeatureLoader`: a feature is one `.cs` file; manifest + spec folded in (attributes / leading comment).
- Sources: disk (`features/` folder), marketplace, Creator — all go through the same load path into state.
- Replace "publish shipped apps at startup" and `ShippedCatalog` with the disk source.
- `dotnet run digitalbrain.cs`: file-based entry that starts AppHost/kernel; first feature loads `features/`.
- Separate design pass before starting (it reshapes `IApp` bundle) — brainstorm, then plan.

## 6. Verification evidence per phase

| Phase | MCP drive | Telemetry |
|---|---|---|
| 1 | `neurons_get` on new-format keys | no deserialization errors at activation |
| 2 | create app → `neurons_list` shows it; `Running()` shows it while active | `InstanceCreated` publish span; kernel register span |
| 3 | each replaced index's old caller path works via kernel | no calls to deleted grain types |
| 4 | `neurons_search` from MCP without local DLL scan | type query span from MCP → silo |
| 5 | forbidden call refused with `{Type}: {Message}` | access-denied structured log with caller + key |
| 6 | feature file on disk appears as running app after start | feature-load spans at boot |

## 7. Risks and open items

- **Write funnel uniqueness.** If some neurons write state outside the base funnel, registration misses
  them. Phase 0 must prove one funnel or create it.
- **Partition hot spots.** Types with very many instances (messages?) — if a type is high-cardinality,
  it should probably not be a neuron per item. Flag any type over ~10k instances per brain in Phase 3.
- **`IManagementGrain` cost.** Cluster fan-out; on-demand only. If per-brain liveness becomes hot, add an
  in-memory per-brain counter in the kernel — not before.
- **Platform ring after Phase 5.** With kernel types not being neurons, the Platform ring may reduce to
  "kernel implementation" or merge into Framework. Decide at end of Phase 5, not before.
- **Revisions duplication** (`IRevisions` grain vs launcher `Revisions`): who owns "which revision is
  live" — kernel or launcher? Decide in Phase 3.
- **Marketplace** is platform-wide, not per brain — its listings are instances of a platform brain
  (`platform/marketplace/{id}`)? Decide in Phase 3.
- **Brains list:** `kernel.Identity` owns brains; a user's brains = instances of `IBrain` across brains
  they're a member of. Confirm in Phase 5.
- **Script-defined neurons** — deferred fork; feature type layer is ready for it.
- **Dart mirrors** of every reshaped record change in the same commit as the C# change.
