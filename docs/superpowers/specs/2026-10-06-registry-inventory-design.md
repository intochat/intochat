# IRegistry: the brain inventory over the platform's own catalogs

Ratified in design discussion 2026-10-06. Successor to the deleted Registry module's one
legitimate question ("what is in this brain?"), answered without the deleted module's
disease: no new state, no mirrors, no per-module coverage machinery.

## The principle

Two catalogs already hold the truth, maintained by the platform itself:

1. **The grain storage** — every persisted neuron is a row, keyed by grain type + brain-scoped
   key. Durable existence *is* this catalog; nothing can exist durably without appearing in it.
2. **The Orleans activation directory** — every grain active in memory right now, cluster-wide,
   via the management grain census. Liveness *is* this catalog; processes are caches and the
   runtime owns the cache.

Everything the old Registry ledger did wrong — mirroring a catalog it didn't own, with
observation signals, dedup hacks and drift — is avoided by not mirroring: the Registry is a
**query surface**, not a store. Activation signals, existence signals, inventory neurons, and
mandatory per-module providers are explicitly rejected designs (each exists only to keep a
copy honest; there is no copy).

## The contract (Framework ring)

```csharp
namespace DigitalBrain.Framework;

// Injectable by any module that references the Framework, like ModuleInventory.
// Scope always comes from the kernel-stamped ambient caller: results are the caller's
// brain only, cross-brain enumeration is impossible by construction.
public interface IRegistry
{
    // From the storage catalog: every persisted neuron in the ambient brain.
    Task<NeuronRecord[]> Persisted(string? typeId = null, CancellationToken ct = default);
    // From the activation directory: neurons of this brain active in memory right now.
    Task<NeuronRecord[]> Active(string? typeId = null, CancellationToken ct = default);
}

[GenerateSerializer, Alias("registry.neuron-record")]
public sealed record NeuronRecord(
    [property: Id(0)] string TypeId,      // grain type id, e.g. "timer.reminder"
    [property: Id(1)] string Key,         // full grain key
    [property: Id(2)] string Contract);   // human/contract name via the shared contract catalog
```

- `IRegistry` is a DI service, **not** a neuron contract: scripts cannot see it. A
  script-facing door is a separate, future ratification.
- The CLR name `IRegistry` is free (the old module's interface was deleted with #148);
  there is no persisted state and no identity to migrate.
- Type ids resolve to contract names through the one contract walk (#147's shared catalog);
  the catalogs compose, nothing is duplicated.

## The implementation (Platform ring)

`DigitalBrain.Platform` implements `IRegistry`:

- **Persisted**: queries the default grain storage by brain-scope key prefix. The
  brain-scoped key convention becomes a load-bearing fact: a guard test asserts the
  convention and the implementation parses scope strictly (no prefix match leaks across
  brains that share a textual prefix — delimiter-exact).
- **Active**: `IManagementGrain` census (detailed grain statistics), filtered to the
  ambient brain by the same key parsing. Transient result, never persisted, computed per ask.
- **Access**: the ambient `CallerContext` is required; results are scoped to its brain.
  App-stamped callers are allowed (an app may inventory its own brain) unless ratification
  decides otherwise during review; cross-brain requests are structurally impossible because
  the brain is never a parameter.
- **Storage capability**: a storage provider that cannot answer a prefix query does not
  support inventory; the implementation fails loudly ("this deployment's grain storage does
  not support inventory") rather than returning an empty lie. Verifying the default
  (Postgres-backed) storage supports the query is implementation step one.

## Consumers

- **Assistant**: inventory tools ("list neurons", "what reminders exist", "what is active")
  compose `IRegistry` with the existing `IRegistryResourceProvider` enrichment seam —
  existence and liveness from the Registry, rich status (armed, next fire, row counts) from
  the domain's own neurons/providers, which remain **optional enrichment, never coverage**.
- **Any module**: inject `IRegistry` — the explicit requirement this design serves.
- **Shell**: a later `/brains/{id}/registry` HTTP surface can expose the same records;
  out of scope here.

## Explicitly rejected

- Kernel `NeuronCreated`/`NeuronRetired` signals + per-brain Inventory neuron: a mirror of
  the storage catalog with read-model repair as its upkeep. Rejected for the same reason the
  activation ledger was deleted.
- Activation signals / lifecycle observers: a mirror of the activation directory.
- Mandatory per-module resource providers for coverage: coverage is total by construction
  when existence comes from storage itself.
