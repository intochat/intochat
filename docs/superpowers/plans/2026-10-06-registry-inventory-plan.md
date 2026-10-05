# IRegistry implementation plan

Spec: `docs/superpowers/specs/2026-10-06-registry-inventory-design.md`. Branch `os`.
Each task is TDD (facts first), per-project builds/tests, never the .slnx.

## Execution status

- [ ] Task 0 — **BLOCKED**: read-only provider probe exited 1; production is Azure Blob,
  not Postgres, and ModuleTest memory storage has no enumeration API. Key audit also
  found opaque C# keys and reminders outside the grain-state catalog.
  [Evidence and options](2026-10-06-registry-inventory-gate.md).
- [ ] Task 1 — not started (gate)
- [ ] Task 2 — not started (gate)
- [ ] Task 3 — not started (gate)
- [ ] Task 4 — not started (gate)
- [ ] Task 5 — not started (gate)
- [ ] Task 6 — not started (gate)
- [ ] Task 7 — not started (gate; no completion-suite or Aspire claim)

## Task 0 — Feasibility gate: the storage catalog is queryable

Confirm the default grain storage can answer "all state rows whose grain key belongs to
brain X", with exact-delimiter scope matching (not textual prefix). Locate the storage
provider in use (Postgres-backed after table-owners-v1; memory storage in ModuleTest
clusters) and write a throwaway probe or a first failing fact proving a prefix query is
expressible for both. If the production storage cannot express it, STOP and report —
the whole design leans on this.

Also inventory the key conventions actually in use: `BrainScope`-prefixed keys vs the
exceptions (deployment-scoped neurons like `integration/...`, platform singletons).
Decide and record in the spec how non-brain-scoped keys are handled: excluded from
Persisted/Active results (recommended — they are deployment infrastructure, not brain
contents).

## Task 1 — Framework contract

`src/DigitalBrain/Framework/DigitalBrain.Framework/Discovery/IRegistry.cs`:
`IRegistry` + `NeuronRecord` exactly as the spec defines (alias `registry.neuron-record`,
ids 0..2). No implementation in Framework. Facts: serialization identity of NeuronRecord
(architecture suite baseline row).

## Task 2 — Platform implementation: Persisted

`src/DigitalBrain/Platform/DigitalBrain.Platform/Discovery/PlatformRegistry.cs`
implementing `IRegistry`:
- Require ambient `CallerContext`; derive the brain scope from it; brain is never a parameter.
- Query the grain storage for the scope; map rows to `NeuronRecord` (grain type id, key,
  contract name via the shared contract catalog from #147 — unknown types map to the raw
  type id, never dropped).
- `typeId` filter applied server-side.
- Loud failure when the composed storage cannot answer the query.
Register in `PlatformHosting` as `TryAddSingleton<IRegistry, PlatformRegistry>()`.
Facts (ModuleTest, real grains): two brains each persist neurons → each sees only its own;
typeId filter works; a deployment-scoped neuron (integration registration) appears in
neither; unauthenticated caller is refused.

## Task 3 — Platform implementation: Active

Same class: `IManagementGrain.GetDetailedGrainStatistics()` census, map each activation's
grain identity to (typeId, key), apply the same brain-scope parse + filter + typeId filter.
Never persisted; computed per call.
Facts: activate a neuron in brain A and one in brain B → A's Active lists only its own;
a neuron that persisted but was deactivated appears in Persisted and not in Active
(force deactivation or use idle collection in test cluster); census errors surface as
exceptions, not empty results.

## Task 4 — Guard the key convention

The brain-scope key convention is now load-bearing. Add an architecture/OS fact that
asserts the scope-parsing rules used by PlatformRegistry against representative real keys
(app keys, csharp file keys, connection keys, reminder keys, integration keys) so a future
key-format change fails a test instead of silently mis-scoping inventory.

## Task 5 — Assistant tools

Extend the Assistant's discovery tooling with registry tools backed by injected
`IRegistry`:
- `list_neurons` (persisted, optional type filter), `list_active_neurons`.
- Compose with existing `discover_capabilities` enrichment: when the user asks "which
  reminders are there", the tool lists persisted `timer.reminder` records and may enrich
  via the domain's provider/neuron reads. Enrichment stays optional.
Facts: scripted-LLM conversation asking for inventory gets grounded results from two
seeded neurons; brain isolation holds through the tool path.

## Task 6 — Time module enrichment (first consumer proof)

Minimal proof that rich status composes: reminders' own state answers "armed / next fire"
when the assistant asks (direct contract read from the tool or a small
IRegistryResourceProvider in Time — pick the cheaper; do NOT build a reminder index
neuron, existence comes from the Registry now).
Facts: set two reminders, ask the assistant tool, see both with schedule detail.

## Task 7 — Docs and closure

- CLAUDE.md: one sentence in the module-contract area: "the Framework's IRegistry answers
  what exists (storage catalog) and what is active (runtime census) per brain; modules
  inject it; nothing mirrors these catalogs."
- Spec self-review; update the 2026-10-06 spec with Task 0's key-handling decision.
- Run: Framework, Platform, Architecture, Assistant, Time, OS unit suites; both E2E
  suites; `aspire run` smoke (all resources Healthy).

## Out of scope (do not build)

Script-facing IRegistry access; `/brains/{id}/registry` HTTP + shell screen; kernel
existence signals; any inventory/index neuron; per-module provider coverage mandates;
cross-brain or deployment-wide inventory surfaces.
