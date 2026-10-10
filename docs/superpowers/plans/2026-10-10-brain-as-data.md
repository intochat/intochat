# Brain as Data — Master Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A brain becomes data — a key plus encrypted state in the mesh — so any machine trusted with a part's key can lease and run that part, writes are never lost, and no store, silo or node has to be trusted with plaintext.

**Architecture:** Two kinds of cluster (Q24). The platform **trust cluster** holds the sequencer, the storage-node directory, keyrings, the ledger and identity; it persists in `platform` storage and hosts no neuron. Every **machine** (a user's own, a circle member's, or our cloud) is a single-silo cluster (Q12) that hosts the parts it leases; on a machine, the `cluster` grain storage that every neuron already names *is the part log* (Q27). One log entry is one neuron change — its new state record and the signals it publishes, committed together — batched into sealed `IMeshStore` segments (Q23) and anchored by a signed compare-and-set head at the sequencer (Q10, Q18). Own machines hold long leases and journal locally first, so a private brain keeps working through a sequencer outage (Q26). Neurons do not change.

**Tech Stack:** .NET 11 RC, Orleans, `System.Security.Cryptography` (AesGcm, ECDsa P-256, ECDiffieHellman P-256, HKDF, SHA-256), xUnit v3 on Microsoft Testing Platform, `DigitalBrain.Testing.Module`, Aspire + `digitalbrain-mcp` for live verification. Later stages add `System.Net.Quic`, DPAPI/TPM through the Windows module, and Wasmtime.

**Spec:** `docs/superpowers/specs/2026-10-10-federation-circles-mesh-design.md` (Q1–Q27). Mesh store already shipped: `src/DigitalBrain.Platform/DigitalBrain.Platform/Mesh/` behind `src/DigitalBrain.Framework/DigitalBrain.Framework/Mesh/IMeshStore.cs`.

## Stages

| Stage | Tasks | Ends with |
|---|---|---|
| **A. Core in one process** (dev) | 1–16 | trust grains and a machine's part log in one silo; every guarantee testable; live run through `digitalbrain-mcp` |
| **B. Split and connect** | 17–22 | trust cluster and machine hosts as separate processes, linked by the transport — **first release** (Q25 mesh from day 0 on seed nodes) |
| **C. Network** | 23–30 | volunteer storage + credits, circles, public compute |

Stage A is specified to the step. Stages B and C are specified to interfaces, tests and acceptance; each plan in them opens with a **gate task** that measures an unknown (NAT traversal, TPM availability, Wasmtime determinism) and then writes that plan's step-level tasks into this file. That is deliberate: QUIC hole-punching code written before the measurement would be invented, and CLAUDE.md forbids improvising open design fronts.

## Global Constraints

- Rings: vocabulary in `src/DigitalBrain`, contracts module authors use in `src/DigitalBrain.Framework`, implementations in `src/DigitalBrain.Platform` (plain grains, never neurons). Framework never references Platform.
- "Storage is encrypted everywhere with user-held keys." Plaintext and keys never cross a grain call (Q4, Q23); they stay in the silo that holds the part's lease.
- "never start a part without its lease" (Q15). No holder → `ServiceUnavailableException` (503).
- "A write counts once it is in the part's log and the sequencer accepted the version" (Q18), except that an own machine acknowledges once the write is in its local journal (Q26).
- "The core is held only by own machines" (Q11). Cloud machines lease only parts marked not private (Q24).
- Storage names: `DigitalBrainNames.PlatformGrainStorage = "platform"` for trust grains; `DigitalBrainNames.ClusterGrainStorage = "cluster"` for neurons (the part log on a machine; memory storage in plain module tests).
- Seed nodes are registered with owner `platform` (Q25); never invent owner names.
- Grain state uses concrete arrays/dictionaries, never `IReadOnlyList`. No self-calls on non-reentrant grains. No state compatibility, no migrations, no shims.
- Options keys under `DigitalBrain:Platform:*`; every new options file is added to `src/Testing/DigitalBrain.Architecture.Tests/ConfigurationKeyOwners.json`.
- Tests: facts with sentence names, `TestContext.Current.CancellationToken`, real grains through `ModuleTest.Create()`. Build and test per project, never the `.slnx`. Kill any test run over 5 minutes and debug it; never raise a timeout to make a test pass.
- LSP (`findReferences`, `goToDefinition`, `incomingCalls`) before changing an existing symbol; diagnostics clean before each build.
- Commit explicit files only; commit messages end with the repo's attribution line.

## Review Focus

1. **Two machines both believe they hold a part** (lease expired mid-flush, clock skew) → exactly one batch wins the CAS; the loser's writes fail with `LeaseLostException` and are never reported durable. Pinned in Task 11.
2. **An own machine comes back online after another machine took its expired lease** → its journaled writes are not silently merged or dropped: the part refuses to start with `JournalConflictException` and the journal is kept aside. Pinned in Task 12.
3. **A silo dies between sealing a segment and the CAS** → after restart the part reads only what the accepted head reaches (plus, on an own machine, its journal); nothing half-written appears. Pinned in Task 12.
4. **A state write succeeds but its signal is lost** (today's gap in `Neuron<TState>.Save`) → impossible: record and signals are one entry. Pinned in Task 14.
5. **The sequencer serves an older head than this machine already saw** → `RollbackDetectedException`, never a silent run on old state. Pinned in Task 8.

## File structure

| File | Responsibility |
|---|---|
| `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Hosting/DigitalBrainNames.cs` | add `PlatformGrainStorage` |
| `src/DigitalBrain.Framework/DigitalBrain.Framework/Parts/PartId.cs` | the part a brain key belongs to (Q27) |
| `src/DigitalBrain.Framework/DigitalBrain.Framework/Parts/IPartKeys.cs` | the key a lease holder uses |
| `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Signalling/SignalJournal.cs` | signals that ride with a state write, or journal alone |
| `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Neuron.cs` | `PublishAsync`/`Save` journal before notifying |
| `src/DigitalBrain.Framework/DigitalBrain.Framework/Mesh/IMeshStore.cs` | add `Discard` |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Mesh/MeshStore.cs` | random padding, `Discard` |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Keys/*` | custody, keyring, part keys, Shamir |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Sequencer/*` | head record, sequencer grain, machine identity, rollback memory |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartsOptions.cs` | machine role, leases, batching, snapshots |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLogModel.cs` | entries, segments, snapshots, head payload |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLease.cs` | acquire/commit/renew/release |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartJournal.cs` | own-machine local encrypted journal (Q26) |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLog.cs` | open/replay, group commit, snapshots, signals since a seq |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLogs.cs` | one `PartLog` per leased part in this silo |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartGrainStorage.cs` | `IGrainStorage` over `PartLogs` |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartSignalJournal.cs` | `ISignalJournal` over `PartLogs` |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/MachineHosting.cs` | `AddMachinePartLog(silo)`: puts `cluster` storage on the part log |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/PlatformHosting.cs` | registrations |
| `src/DigitalBrain.Platform/DigitalBrain.Platform.Trust/*` | trust grains (sequencer, keyring, node directory, seed shards, ledger/usage, identity authority (Account/Principal/BrainAuthority grains), marketplace, first-party listing registration) and `AddTrustCluster` (Task 18, R33) |
| `src/DigitalBrain.Platform/DigitalBrain.Platform/Trust/*` | `ISequencerClient`, `INodeDirectoryClient`, `IKeyringClient`, `IMarketplaceClient`, grain and gateway implementations, `TrustOptions` (Task 18) |
| `src/DigitalBrain.Trust/*` | trust cluster host (Task 18) |
| `src/DigitalBrain.Framework/DigitalBrain.Framework/Parts/IMasterKeyCustody.cs`, `IMachineSigningKey.cs` | custody and machine signing contracts (Task 19) |
| `src/DigitalBrain.Modules/Microsoft/Windows/DigitalBrain.Modules.Microsoft.Windows/Tpm*.cs` | TPM custody and machine signing key (Task 19) |

---

# Stage A — core in one process

### Task 1: Trust grains get their own storage (Q24)

**Files:**
- Modify: `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Hosting/DigitalBrainNames.cs`
- Modify: every Platform **grain** (not neuron) that names `ClusterGrainStorage`, and the three Mesh grains: `Compute/LedgerGrain.cs`, `Compute/UsageGrain.cs`, `Compute/TraceBudgetGrain.cs`, `Identity/Authority/PrincipalGrain.cs`, `Identity/Authority/AccountGrain.cs`, `Identity/Authority/BrainAuthorityGrain.cs`, `Mesh/StorageNodeDirectoryGrain.cs`, `Mesh/SeedShardGrain.cs` (confirm the list with LSP, Step 1)
- Modify: `src/DigitalBrain.Aspire/DigitalBrain.Aspire.Hosting/DigitalBrainHostingExtensions.cs`, `src/DigitalBrain.Aspire/DigitalBrain.Aspire.Server/ServerHostingExtensions.cs`, `src/DigitalBrain.Modules/Microsoft/Aspire/DigitalBrain.Modules.Microsoft.Aspire/Candidates/DockerCandidateHost.cs`, `src/Testing/DigitalBrain.Testing.Module/ModuleTest.cs`
- Create: `src/Testing/DigitalBrain.Architecture.Tests/StorageRingFacts.cs`

**Interfaces:**
- Produces: `public const string PlatformGrainStorage = "platform";` in `DigitalBrainNames`. Rule: a type deriving from `DigitalBrain.Framework.Neuron` persists in `ClusterGrainStorage`; a plain grain in `DigitalBrain.Platform` persists in `PlatformGrainStorage`. Kernel grains in `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Kernel/KernelGrains.cs` are the brain's own registry, so they stay on `cluster` (they belong to the core part).

- [ ] **Step 1: Map.** LSP `findReferences` on `DigitalBrainNames.ClusterGrainStorage`. For each hit, `goToDefinition` on the class's base type: keep `Neuron`-derived and `KernelGrains.cs` sites on `cluster`; list every `Grain`-derived site in `DigitalBrain.Platform`.

- [ ] **Step 2: Failing architecture fact**

```csharp
using System.Reflection;
using DigitalBrain.Framework;

namespace DigitalBrain.Architecture.Tests;

public sealed class StorageRingFacts
{
    [Fact]
    public void NeuronsPersistInClusterStorageAndPlatformGrainsInPlatformStorage()
    {
        var platform = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        var offenders = new[] { typeof(Neuron).Assembly, platform }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => (type, storage: parameter.GetCustomAttribute<PersistentStateAttribute>()?.StorageName)))
            .Where(site => site.storage is not null)
            .Where(site => typeof(Neuron).IsAssignableFrom(site.type) || site.type.Assembly != platform
                ? site.storage != DigitalBrainNames.ClusterGrainStorage
                : site.storage != DigitalBrainNames.PlatformGrainStorage)
            .Select(site => $"{site.type.FullName} -> {site.storage}")
            .ToArray();
        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders));
    }
}
```

  (If the Architecture test project does not reference `DigitalBrain.Platform`, add the `ProjectReference`; LSP-check how `PersistedStateFacts` loads assemblies and follow it if it differs.)

- [ ] **Step 3: Run** `dotnet test --project src/Testing/DigitalBrain.Architecture.Tests -- --filter-method "*NeuronsPersistInClusterStorage*"` → FAIL listing the Platform grains.

- [ ] **Step 4: Implement.** Add the constant. Switch each listed Platform grain to `DigitalBrainNames.PlatformGrainStorage`. Register the provider wherever `cluster` is registered today, pointing at the same backing store for now:
  - `ModuleTest.cs`: `silo.AddMemoryGrainStorage(DigitalBrainNames.PlatformGrainStorage);` next to the `ClusterGrainStorage` line.
  - `DigitalBrainHostingExtensions.cs`: `.WithMemoryGrainStorage(DigitalBrainNames.PlatformGrainStorage)` on the memory path; `.WithGrainStorage(DigitalBrainNames.PlatformGrainStorage, grainState)` on the Azure path; and the `Orleans__GrainStorage__{PlatformGrainStorage}__ServiceKey` environment line beside the cluster one.
  - `ServerHostingExtensions.cs`: the keyed serializer and `AzureBlobStorageOptions` registration for `PlatformGrainStorage`, copied from the `ClusterGrainStorage` block.
  - `DockerCandidateHost.cs`: the two `Orleans__GrainStorage__{PlatformGrainStorage}__*` entries beside the cluster ones.
  The kernel registration decorator stays on `cluster` only: platform grains are not neurons and are not registered in a brain.

- [ ] **Step 5: Run** the Architecture fact, then the Platform, Framework, Aspire hosting and OS unit suites → PASS (the Architecture suite's six pre-existing failures are unrelated; confirm none mention storage).

- [ ] **Step 6: Commit** the touched files: `git commit -m "Storage: platform grains persist in platform storage, neurons in cluster storage"`.

### Task 2: A part is a key prefix inside a brain (Q27)

**Files:**
- Create: `src/DigitalBrain.Framework/DigitalBrain.Framework/Parts/PartId.cs`
- Test: `src/DigitalBrain.Framework/DigitalBrain.Framework.Tests.Unit/Parts/PartIdFacts.cs`

**Interfaces:**
- Produces: `public sealed record PartId(string BrainId, string? App)` with `Value`, `IsCore`, `Core(brainId)`, `ForApp(brainId, app)`, `static PartId Of(string grainKey)` — throws `ArgumentException` for a key outside every brain (a machine cluster refuses such keys, Q27).

- [ ] **Step 1: Confirm key shapes.** LSP `findReferences` on `BrainScope.Create` and on `IApp`; `goToDefinition` on `BrainScope.IsValidId`. App neurons are `{brain}/apps/{name}`.

- [ ] **Step 2: Failing tests**

```csharp
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Framework.Tests.Unit.Parts;

public sealed class PartIdFacts
{
    [Theory]
    [InlineData("home", "home/core")]
    [InlineData("home/kernel", "home/core")]
    [InlineData("home/connections/gmail/1", "home/core")]
    [InlineData("home/apps", "home/core")]
    [InlineData("home/apps/filmcraft", "home/apps/filmcraft")]
    [InlineData("home/apps/filmcraft/tables/clips", "home/apps/filmcraft")]
    public void AKeyBelongsToItsAppOrToItsBrainsCore(string key, string part) => Assert.Equal(part, PartId.Of(key).Value);

    [Theory]
    [InlineData("")]
    [InlineData("@platform/ledger")]
    [InlineData("bad:brain/x")]
    public void AKeyOutsideEveryBrainIsRefused(string key) => Assert.Throws<ArgumentException>(() => PartId.Of(key));
}
```

- [ ] **Step 3: Run** `dotnet test --project src/DigitalBrain.Framework/DigitalBrain.Framework.Tests.Unit -- --filter-class "*PartIdFacts*"` → FAIL.

- [ ] **Step 4: Implement**

```csharp
namespace DigitalBrain.Framework.Parts;

// The unit a machine leases and a key encrypts (Q9, Q27): the brain's core, or one app. A neuron belongs to the
// part whose key prefix it carries, exactly as it belongs to its brain; anything outside apps/{app}/ is core,
// which only the owner's machines hold.
[GenerateSerializer, Alias("framework.part-id")]
public sealed record PartId([property: Id(0)] string BrainId, [property: Id(1)] string? App)
{
    public string Value => App is null ? $"{BrainId}/core" : $"{BrainId}/apps/{App}";

    public bool IsCore => App is null;

    public static PartId Core(string brainId) => new(brainId, null);

    public static PartId ForApp(string brainId, string app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        return new(brainId, app);
    }

    public static PartId Of(string grainKey)
    {
        var segments = (grainKey ?? "").Split('/');
        if (!BrainScope.IsValidId(segments[0])) { throw new ArgumentException($"Key '{grainKey}' is outside every brain; a machine hosts only brain parts.", nameof(grainKey)); }
        return segments.Length >= 3 && segments[1] == "apps" && segments[2].Length > 0 ? ForApp(segments[0], segments[2]) : Core(segments[0]);
    }

    public override string ToString() => Value;
}
```

- [ ] **Step 5: Run** → PASS. **Step 6: Commit** both files: `git commit -m "Parts: a part is the core or an app, by the key prefix a neuron already carries"`.

### Task 3: Mesh segments never share a shard, and can be discarded

**Files:**
- Modify: `src/DigitalBrain.Framework/DigitalBrain.Framework/Mesh/IMeshStore.cs`
- Modify: `src/DigitalBrain.Platform/DigitalBrain.Platform/Mesh/MeshStore.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Mesh/MeshStoreFacts.cs`, `AuditRepairFacts.cs`

**Interfaces:**
- Produces: `Task Discard(byte[] key, MeshRef reference, CancellationToken cancellationToken = default)` on `IMeshStore`: drops every shard and manifest replica of the segment; idempotent.

- [ ] **Step 1: Failing tests** (add to `MeshStoreFacts`)

```csharp
    [Fact(Timeout = 60_000)]
    public async Task TwoSegmentsOfTheSameShortDataShareNoShard()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(41, ct, options => { options.DataShards = 40; options.ParityShards = 1; });
        await using var _ = brain;

        var a = await Manifest(brain, Key, await store.Write(Key, "part/log/1", [7], ct));
        var b = await Manifest(brain, Key, await store.Write(Key, "part/log/2", [7], ct));

        Assert.Empty(a.Shards.Select(s => s.Cid).Intersect(b.Shards.Select(s => s.Cid)));
    }

    [Fact(Timeout = 60_000)]
    public async Task DiscardingASegmentRemovesItsShardsAndLeavesOthersReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, store, _) = await StartAsync(8, ct);
        await using var _ = brain;
        var gone = await store.Write(Key, "part/log/1", [1, 2, 3], ct);
        var kept = await store.Write(Key, "part/log/2", [4, 5, 6], ct);
        var shards = (await Manifest(brain, Key, gone)).Shards;

        await store.Discard(Key, gone, ct);
        await store.Discard(Key, gone, ct);

        foreach (var shard in shards) { Assert.Null(await Shard(brain, shard.NodeId, shard.Cid).Get()); }
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => store.Read(Key, gone, ct));
        Assert.Equal(new byte[] { 4, 5, 6 }, await store.Read(Key, kept, ct));
    }
```

  Delete `AuditRepairFacts.IdenticalShardsOfTwoSegmentsSurviveEitherOneBeingAudited`: its premise (shared zero shards) no longer exists.

- [ ] **Step 2: Run** `dotnet test --project src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit -- --filter-class "*MeshStoreFacts*"` → FAIL.

- [ ] **Step 3: Implement.** In `IMeshStore`, after `Repair`:

```csharp
    // Drops a segment's shards and manifest replicas. Safe because segments never share a shard (random padding).
    Task Discard(byte[] key, MeshRef reference, CancellationToken cancellationToken = default);
```

  In `MeshStore.Write`, replace `var shards = rs.Encode(sealedData, out var shardSize);` with `var shards = rs.Encode(Padded(sealedData, rs.DataShards), out var shardSize);` (keep `SealedLength = sealedData.Length`), and add:

```csharp
    // Padding is never read (Join stops at SealedLength); random bytes keep every segment's shards unique.
    private static byte[] Padded(byte[] sealedData, int dataShards)
    {
        var shardSize = Math.Max(1, (sealedData.Length + dataShards - 1) / dataShards);
        var padded = new byte[shardSize * dataShards];
        sealedData.CopyTo(padded, 0);
        RandomNumberGenerator.Fill(padded.AsSpan(sealedData.Length));
        return padded;
    }

    public async Task Discard(byte[] key, MeshRef reference, CancellationToken cancellationToken = default)
    {
        SegmentManifest manifest;
        try { manifest = await ReadManifest(key, reference, cancellationToken); }
        catch (ServiceUnavailableException) { return; }
        await Task.WhenAll(manifest.Shards.Select(shard => Shard(shard.NodeId, shard.Cid).Drop()));
        await Task.WhenAll(reference.ManifestNodes.Select(nodeId => Shard(nodeId, reference.ManifestCid).Drop()));
    }
```

- [ ] **Step 4: Run** `... -- --filter-namespace "*Mesh*"` → PASS. **Step 5: Commit** the four files: `git commit -m "Mesh: random padding keeps every segment's shards unique, so a segment can be discarded"`.

---

## Part keys (Tasks 4–6)

### Task 4: Master key custody

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Keys/IMasterKeyCustody.cs`, `ConfiguredMasterKeyCustody.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Keys/MasterKeyCustodyFacts.cs`

**Interfaces:**
- Produces: `internal interface IMasterKeyCustody { byte[] Wrap(string accountId, string context, byte[] secret); byte[] Unwrap(string accountId, string context, byte[] wrapped); }`.

- [ ] **Step 1: LSP.** `goToDefinition` on `AddMasterKeyWrapper` (`Secrets/MasterKeyServiceCollectionExtensions.cs`) and `findReferences` on `DigitalBrainNames.MasterKeyConfigurationKey`. If the existing wrapper already wraps bytes under the configured master key, `ConfiguredMasterKeyCustody` derives from the same root it uses; keep the interface below either way.

- [ ] **Step 2: Failing tests**

```csharp
using System.Security.Cryptography;
using DigitalBrain.Platform.Keys;

namespace DigitalBrain.Platform.Tests.Unit.Keys;

public sealed class MasterKeyCustodyFacts
{
    private static ConfiguredMasterKeyCustody Custody() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void AWrappedSecretUnwrapsForTheSameAccountAndContext()
    {
        var custody = Custody();
        var secret = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(secret, custody.Unwrap("alice", "part-key|home/core", custody.Wrap("alice", "part-key|home/core", secret)));
    }

    [Fact]
    public void AnotherAccountOrContextCannotUnwrapIt()
    {
        var custody = Custody();
        var wrapped = custody.Wrap("alice", "part-key|home/core", [1, 2, 3]);
        Assert.ThrowsAny<CryptographicException>(() => custody.Unwrap("bob", "part-key|home/core", wrapped));
        Assert.ThrowsAny<CryptographicException>(() => custody.Unwrap("alice", "part-key|home/apps/x", wrapped));
    }
}
```

- [ ] **Step 3: Run** `... -- --filter-class "*MasterKeyCustodyFacts*"` → FAIL.

- [ ] **Step 4: Implement**

```csharp
// IMasterKeyCustody.cs
namespace DigitalBrain.Platform.Keys;

// Holds each user's master key (Q3, Q6). Callers hand it secrets to wrap; the master key never leaves it.
// Stage A derives it from a configured root; Task 19 moves custody into the machine's TPM.
internal interface IMasterKeyCustody
{
    byte[] Wrap(string accountId, string context, byte[] secret);

    byte[] Unwrap(string accountId, string context, byte[] wrapped);
}
```

```csharp
// ConfiguredMasterKeyCustody.cs
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Platform.Mesh;

namespace DigitalBrain.Platform.Keys;

// Development custody: one configured root, an HKDF-derived master key per account. Not user-held; Task 19 is.
internal sealed class ConfiguredMasterKeyCustody(byte[] root) : IMasterKeyCustody
{
    public byte[] Wrap(string accountId, string context, byte[] secret) => SegmentSeal.Close(Master(accountId), context, secret);

    public byte[] Unwrap(string accountId, string context, byte[] wrapped) => SegmentSeal.Open(Master(accountId), context, wrapped);

    private byte[] Master(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, info: Encoding.UTF8.GetBytes($"master|{accountId}"));
    }
}
```

- [ ] **Step 5: Run** → PASS. **Step 6: Commit** the three files: `git commit -m "Keys: master key custody wraps secrets per account and context"`.

### Task 5: Keyring and part keys

**Files:**
- Create: `src/DigitalBrain.Framework/DigitalBrain.Framework/Parts/IPartKeys.cs`
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Keys/KeyringGrain.cs`, `PartKeys.cs`
- Modify: `src/DigitalBrain.Platform/DigitalBrain.Platform/PlatformHosting.cs`, `src/Testing/DigitalBrain.Testing.Module/ModuleTest.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Keys/PartKeyFacts.cs`

**Interfaces:**
- Consumes: `PartId` (Task 2), `IMasterKeyCustody` (Task 4).
- Produces: `public interface IPartKeys { Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default); Task<byte[]> Rotate(PartId part, string ownerAccount, CancellationToken cancellationToken = default); }`; `internal interface IKeyringGrain : IGrainWithStringKey { Task<byte[]?> Get(string partId); Task<bool> TryAdd(string partId, byte[] wrapped); Task Replace(string partId, byte[] wrapped); }` keyed by brain id, persisted in `platform` storage (a trust-cluster grain holding ciphertext only).

- [ ] **Step 1: Failing tests**

```csharp
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Keys;

public sealed class PartKeyFacts
{
    [Fact(Timeout = 60_000)]
    public async Task APartKeyIsCreatedOnceAndStoredOnlyWrapped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var keys = brain.SiloServices.GetRequiredService<IPartKeys>();
        var part = PartId.Core("home");

        var first = await keys.Key(part, "alice", ct);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, await keys.Key(part, "alice", ct));
        var stored = await brain.Grains.GetGrain<IKeyringGrain>("home").Get(part.Value);
        Assert.NotNull(stored);
        Assert.True(stored.AsSpan().IndexOf(first) < 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task EachPartHasItsOwnKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var keys = brain.SiloServices.GetRequiredService<IPartKeys>();

        Assert.NotEqual(await keys.Key(PartId.Core("home"), "alice", ct), await keys.Key(PartId.ForApp("home", "filmcraft"), "alice", ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task RotatingAPartKeyReplacesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var keys = brain.SiloServices.GetRequiredService<IPartKeys>();
        var part = PartId.ForApp("home", "filmcraft");
        var old = await keys.Key(part, "alice", ct);

        var rotated = await keys.Rotate(part, "alice", ct);

        Assert.NotEqual(old, rotated);
        Assert.Equal(rotated, await keys.Key(part, "alice", ct));
    }
}
```

- [ ] **Step 2: Run** `... -- --filter-class "*PartKeyFacts*"` → FAIL.

- [ ] **Step 3: Implement**

```csharp
// Framework/Parts/IPartKeys.cs
namespace DigitalBrain.Framework.Parts;

// The key that encrypts one part (Q6). Only a silo holding the part's lease asks for it; no grain call carries it.
public interface IPartKeys
{
    Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default);

    // Revoking a circle rotates one part key; the part log re-seals from its next snapshot.
    Task<byte[]> Rotate(PartId part, string ownerAccount, CancellationToken cancellationToken = default);
}
```

```csharp
// Platform/Keys/KeyringGrain.cs
using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Keys;

// Wrapped part keys of one brain, in the trust cluster: ciphertext only, so holding it teaches us nothing.
internal interface IKeyringGrain : IGrainWithStringKey
{
    Task<byte[]?> Get(string partId);

    Task<bool> TryAdd(string partId, byte[] wrapped);

    Task Replace(string partId, byte[] wrapped);
}

[GrainType("platform.keyring")]
internal sealed class KeyringGrain(
    [PersistentState("keyring", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<KeyringState> store) : Grain, IKeyringGrain
{
    public Task<byte[]?> Get(string partId) => Task.FromResult(store.State.Keys.GetValueOrDefault(partId));

    public async Task<bool> TryAdd(string partId, byte[] wrapped)
    {
        if (store.State.Keys.ContainsKey(partId)) { return false; }
        store.State.Keys[partId] = wrapped;
        await store.WriteStateAsync();
        return true;
    }

    public async Task Replace(string partId, byte[] wrapped)
    {
        store.State.Keys[partId] = wrapped;
        await store.WriteStateAsync();
    }
}

[GenerateSerializer, Alias("platform.keyring-state")]
internal sealed class KeyringState
{
    [Id(0)] public Dictionary<string, byte[]> Keys { get; set; } = new(StringComparer.Ordinal);
}
```

```csharp
// Platform/Keys/PartKeys.cs
using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Keys;

internal sealed class PartKeys(IGrainFactory grains, IMasterKeyCustody custody) : IPartKeys
{
    public async Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default)
    {
        var keyring = grains.GetGrain<IKeyringGrain>(part.BrainId);
        if (await keyring.Get(part.Value) is { } wrapped) { return custody.Unwrap(ownerAccount, Context(part), wrapped); }
        var key = RandomNumberGenerator.GetBytes(32);
        // Two silos racing to create: the first add wins and both use its key.
        return await keyring.TryAdd(part.Value, custody.Wrap(ownerAccount, Context(part), key))
            ? key
            : custody.Unwrap(ownerAccount, Context(part), (await keyring.Get(part.Value))!);
    }

    public async Task<byte[]> Rotate(PartId part, string ownerAccount, CancellationToken cancellationToken = default)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await grains.GetGrain<IKeyringGrain>(part.BrainId).Replace(part.Value, custody.Wrap(ownerAccount, Context(part), key));
        return key;
    }

    private static string Context(PartId part) => $"part-key|{part.Value}";
}
```

  In `AddDigitalBrainPlatform`, after the mesh registrations:

```csharp
        services.TryAddSingleton<Keys.IMasterKeyCustody>(provider => new Keys.ConfiguredMasterKeyCustody(Convert.FromHexString(
            provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()[DigitalBrainNames.MasterKeyConfigurationKey]
                ?? throw new InvalidOperationException($"Set {DigitalBrainNames.MasterKeyConfigurationKey} to 64 hex characters."))));
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IPartKeys, Keys.PartKeys>();
```

  In `ModuleTest`, if Step 1 of Task 4 showed it sets no master key, add `[DigitalBrainNames.MasterKeyConfigurationKey] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))` to the silo's configuration.

- [ ] **Step 4: Run** `... -- --filter-namespace "*Keys*"` → PASS. **Step 5: Commit** the six files: `git commit -m "Keys: part keys are random, wrapped by the owner's master key, kept only as ciphertext"`.

### Task 6: Shamir recovery shares

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Keys/Shamir.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Keys/ShamirFacts.cs`

**Interfaces:**
- Produces: `internal static class Shamir { byte[][] Split(byte[] secret, int threshold, int shares); byte[] Combine(byte[][] shares); }`; a share is `[x, y0..yn]`, x in 1..255.

- [ ] **Step 1: Failing tests**

```csharp
using System.Security.Cryptography;
using DigitalBrain.Platform.Keys;

namespace DigitalBrain.Platform.Tests.Unit.Keys;

public sealed class ShamirFacts
{
    [Fact]
    public void AnyTwoOfThreeSharesRecoverTheKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var shares = Shamir.Split(key, 2, 3);
        Assert.Equal(key, Shamir.Combine([shares[0], shares[1]]));
        Assert.Equal(key, Shamir.Combine([shares[0], shares[2]]));
        Assert.Equal(key, Shamir.Combine([shares[1], shares[2]]));
    }

    [Fact]
    public void OneShareAloneDoesNotRecoverTheKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(key, Shamir.Combine([Shamir.Split(key, 2, 3)[0]]));
    }

    [Fact]
    public void AThresholdAboveTheShareCountIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Shamir.Split(new byte[32], 4, 3));
}
```

- [ ] **Step 2: Run** `... -- --filter-class "*ShamirFacts*"` → FAIL.

- [ ] **Step 3: Implement**

```csharp
using System.Security.Cryptography;

namespace DigitalBrain.Platform.Keys;

// Master key recovery (spec Shape): a random polynomial per byte over GF(2^8) (polynomial 0x11D, the field
// ReedSolomon uses); share i is its value at x = i, and any `threshold` shares interpolate back to x = 0.
internal static class Shamir
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static Shamir()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) { x ^= 0x11D; }
        }
        for (int i = 255; i < 512; i++) { Exp[i] = Exp[i - 255]; }
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];

    public static byte[][] Split(byte[] secret, int threshold, int shares)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, shares);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shares, 255);
        var result = Enumerable.Range(1, shares).Select(x => { var share = new byte[secret.Length + 1]; share[0] = (byte)x; return share; }).ToArray();
        var coefficients = new byte[threshold];
        for (int b = 0; b < secret.Length; b++)
        {
            coefficients[0] = secret[b];
            RandomNumberGenerator.Fill(coefficients.AsSpan(1));
            foreach (var share in result)
            {
                byte y = 0;
                for (int c = threshold - 1; c >= 0; c--) { y = (byte)(Mul(y, share[0]) ^ coefficients[c]); }
                share[b + 1] = y;
            }
        }
        CryptographicOperations.ZeroMemory(coefficients);
        return result;
    }

    public static byte[] Combine(byte[][] shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        var secret = new byte[shares[0].Length - 1];
        for (int b = 0; b < secret.Length; b++)
        {
            byte value = 0;
            for (int i = 0; i < shares.Length; i++)
            {
                byte basis = 1;
                for (int j = 0; j < shares.Length; j++)
                {
                    if (i != j) { basis = Mul(basis, Div(shares[j][0], (byte)(shares[j][0] ^ shares[i][0]))); }
                }
                value ^= Mul(shares[i][b + 1], basis);
            }
            secret[b] = value;
        }
        return secret;
    }
}
```

- [ ] **Step 4: Run** → PASS. **Step 5: Commit** both files: `git commit -m "Keys: Shamir 2-of-3 shares recover a master key"`.

---

## Sequencer (Tasks 7–9)

### Task 7: Machine identity

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Sequencer/MachineIdentity.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Sequencer/MachineIdentityFacts.cs`

**Interfaces:**
- Produces: `internal sealed class MachineIdentity : IDisposable { string MachineId; byte[] PublicKey; byte[] Sign(byte[] data); static bool Verify(byte[] publicKey, byte[] data, byte[] signature); static MachineIdentity LoadOrCreate(string path); }` — ECDsa P-256; `MachineId` = first 16 hex chars of SHA-256 of the SubjectPublicKeyInfo.

- [ ] **Step 1: Failing test**

```csharp
using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Tests.Unit.Sequencer;

public sealed class MachineIdentityFacts
{
    [Fact]
    public void AnIdentityIsStableAcrossLoadsAndItsSignaturesVerify()
    {
        var path = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}.key");
        try
        {
            using var first = MachineIdentity.LoadOrCreate(path);
            using var again = MachineIdentity.LoadOrCreate(path);
            Assert.Equal(first.MachineId, again.MachineId);
            var signature = first.Sign([1, 2, 3]);
            Assert.True(MachineIdentity.Verify(again.PublicKey, [1, 2, 3], signature));
            Assert.False(MachineIdentity.Verify(again.PublicKey, [1, 2, 4], signature));
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run** `... -- --filter-class "*MachineIdentityFacts*"` → FAIL.

- [ ] **Step 3: Implement**

```csharp
using System.Security.Cryptography;

namespace DigitalBrain.Platform.Sequencer;

// This machine's signing key: it signs the head records it writes (Q10), so the sequencer cannot forge them.
// Stage A keeps the PKCS#8 key in a file; Task 19 moves it into the machine's TPM.
internal sealed class MachineIdentity : IDisposable
{
    private readonly ECDsa _key;

    private MachineIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        MachineId = Convert.ToHexStringLower(SHA256.HashData(PublicKey))[..16];
    }

    public string MachineId { get; }

    public byte[] PublicKey { get; }

    public static MachineIdentity LoadOrCreate(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path)) { key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _); }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, key.ExportPkcs8PrivateKey());
        }
        return new MachineIdentity(key);
    }

    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public void Dispose() => _key.Dispose();
}
```

- [ ] **Step 4: Run** → PASS. **Step 5: Commit** both files: `git commit -m "Sequencer: a machine identity signs and verifies head records"`.

### Task 8: Head record, sequencer grain, rollback memory

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Sequencer/HeadRecord.cs`, `SequencerGrain.cs`, `SeenVersions.cs`
- Create: `src/DigitalBrain/Neurons/RollbackDetectedException.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Sequencer/SequencerFacts.cs`, `SeenVersionsFacts.cs`

**Interfaces:**
- Consumes: `MachineIdentity` (Task 7).
- Produces:
  - `HeadRecord` (`[GenerateSerializer]`): `PartHash, Version, Holder, HolderKey, LeaseExpiry, Payload, Signature`; `byte[] SignedBytes()`; `static string HashOf(string partId)`.
  - `ISequencerGrain : IGrainWithStringKey` (key = `PartHash`, `platform` storage): `Task<HeadRecord?> Get()`; `Task<bool> CompareAndSet(long expectedVersion, HeadRecord next)` — accepts only `next.Version == expectedVersion + 1`, current version == `expectedVersion`, `next.PartHash` == grain key, a valid signature by `next.HolderKey`, and no live lease held by another machine.
  - `SeenVersions(string path)`: `Check(partHash, version)` throws `RollbackDetectedException` below the highest seen; `Saw(partHash, version)` persists it atomically.
  - `public sealed class RollbackDetectedException(string message) : Exception` in namespace `DigitalBrain`, `[GenerateSerializer, Alias("framework.rollback-detected")]`.

- [ ] **Step 1: Failing tests**

```csharp
using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Tests.Unit.Sequencer;

public sealed class SequencerFacts
{
    internal static MachineIdentity Machine() => MachineIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"m-{Guid.NewGuid():N}.key"));

    internal static HeadRecord Record(MachineIdentity machine, string partHash, long version, TimeSpan lease, byte[]? payload = null)
    {
        var record = new HeadRecord
        {
            PartHash = partHash, Version = version, Holder = machine.MachineId, HolderKey = machine.PublicKey,
            LeaseExpiry = DateTimeOffset.UtcNow + lease, Payload = payload ?? [],
        };
        record.Signature = machine.Sign(record.SignedBytes());
        return record;
    }

    [Fact(Timeout = 60_000)]
    public async Task VersionsAdvanceOnlyByCompareAndSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var machine = Machine();
        var sequencer = brain.Grains.GetGrain<ISequencerGrain>("p1");

        Assert.True(await sequencer.CompareAndSet(0, Record(machine, "p1", 1, TimeSpan.FromMinutes(1))));
        Assert.False(await sequencer.CompareAndSet(0, Record(machine, "p1", 1, TimeSpan.FromMinutes(1))));
        Assert.True(await sequencer.CompareAndSet(1, Record(machine, "p1", 2, TimeSpan.FromMinutes(1))));
        Assert.Equal(2, (await sequencer.Get())!.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task AForgedOrTamperedRecordIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var machine = Machine();
        var sequencer = brain.Grains.GetGrain<ISequencerGrain>("p1");
        var record = Record(machine, "p1", 1, TimeSpan.FromMinutes(1), [1]);
        record.Payload = [2];

        Assert.False(await sequencer.CompareAndSet(0, record));
        Assert.Null(await sequencer.Get());
    }

    [Fact(Timeout = 60_000)]
    public async Task AnotherMachineCannotTakeALiveLeaseButCanTakeAnExpiredOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var alice = Machine();
        using var bob = Machine();
        var live = brain.Grains.GetGrain<ISequencerGrain>("live");
        var expired = brain.Grains.GetGrain<ISequencerGrain>("expired");
        await live.CompareAndSet(0, Record(alice, "live", 1, TimeSpan.FromMinutes(1)));
        await expired.CompareAndSet(0, Record(alice, "expired", 1, TimeSpan.FromSeconds(-1)));

        Assert.False(await live.CompareAndSet(1, Record(bob, "live", 2, TimeSpan.FromMinutes(1))));
        Assert.True(await expired.CompareAndSet(1, Record(bob, "expired", 2, TimeSpan.FromMinutes(1))));
    }
}
```

```csharp
using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Tests.Unit.Sequencer;

public sealed class SeenVersionsFacts
{
    [Fact]
    public void AnOlderHeadThanOnceSeenIsARollbackEvenAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seen-{Guid.NewGuid():N}.json");
        try
        {
            new SeenVersions(path).Saw("p", 7);
            var restarted = new SeenVersions(path);

            restarted.Check("p", 7);
            restarted.Check("p", 9);
            Assert.Throws<RollbackDetectedException>(() => restarted.Check("p", 6));
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run** `... -- --filter-namespace "*Sequencer*"` → FAIL.

- [ ] **Step 3: Implement**

```csharp
// HeadRecord.cs
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Platform.Sequencer;

// One part's head (Q10): who holds the lease until when, and a payload sealed with the part key that names the
// log head. Signed by the holder; the sequencer can verify and withhold it, never read or forge it.
[GenerateSerializer, Alias("platform.sequencer.head")]
internal sealed class HeadRecord
{
    [Id(0)] public string PartHash { get; set; } = "";
    [Id(1)] public long Version { get; set; }
    [Id(2)] public string Holder { get; set; } = "";
    [Id(3)] public byte[] HolderKey { get; set; } = [];
    [Id(4)] public DateTimeOffset LeaseExpiry { get; set; }
    [Id(5)] public byte[] Payload { get; set; } = [];
    [Id(6)] public byte[] Signature { get; set; } = [];

    public static string HashOf(string partId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partId)));

    public byte[] SignedBytes()
    {
        using var buffer = new MemoryStream();
        void Field(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }
        Span<byte> number = stackalloc byte[8];
        Field(Encoding.UTF8.GetBytes(PartHash));
        BinaryPrimitives.WriteInt64BigEndian(number, Version); Field(number);
        Field(Encoding.UTF8.GetBytes(Holder));
        Field(HolderKey);
        BinaryPrimitives.WriteInt64BigEndian(number, LeaseExpiry.ToUnixTimeMilliseconds()); Field(number);
        Field(Payload);
        return buffer.ToArray();
    }
}
```

```csharp
// SequencerGrain.cs
using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Sequencer;

// Per part: the signed head with compare-and-set (Q10). A trust-cluster grain: it holds nothing it could misuse.
internal interface ISequencerGrain : IGrainWithStringKey
{
    Task<HeadRecord?> Get();

    Task<bool> CompareAndSet(long expectedVersion, HeadRecord next);
}

[GrainType("platform.sequencer")]
internal sealed class SequencerGrain(
    [PersistentState("sequencer", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<SequencerState> store,
    TimeProvider time) : Grain, ISequencerGrain
{
    public Task<HeadRecord?> Get() => Task.FromResult(store.State.Head);

    public async Task<bool> CompareAndSet(long expectedVersion, HeadRecord next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var current = store.State.Head;
        if ((current?.Version ?? 0) != expectedVersion || next.Version != expectedVersion + 1) { return false; }
        if (next.PartHash != this.GetPrimaryKeyString()) { return false; }
        if (!MachineIdentity.Verify(next.HolderKey, next.SignedBytes(), next.Signature)) { return false; }
        if (current is not null && current.Holder != next.Holder && current.LeaseExpiry > time.GetUtcNow()) { return false; }
        store.State.Head = next;
        await store.WriteStateAsync();
        return true;
    }
}

[GenerateSerializer, Alias("platform.sequencer-state")]
internal sealed class SequencerState
{
    [Id(0)] public HeadRecord? Head { get; set; }
}
```

```csharp
// SeenVersions.cs
using System.Text.Json;

namespace DigitalBrain.Platform.Sequencer;

// The highest head version this machine has seen per part, kept on the machine (Q10): the sequencer can
// withhold new heads but cannot serve an old one unnoticed.
internal sealed class SeenVersions(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, long>? _seen;

    public void Check(string partHash, long version)
    {
        lock (_gate)
        {
            if (Seen().GetValueOrDefault(partHash) > version)
            { throw new RollbackDetectedException($"Part {partHash} was served version {version} after version {Seen()[partHash]}."); }
        }
    }

    public void Saw(string partHash, long version)
    {
        lock (_gate)
        {
            var seen = Seen();
            if (seen.GetValueOrDefault(partHash) >= version) { return; }
            seen[partHash] = version;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(seen));
            File.Move(temp, path, overwrite: true);
        }
    }

    private Dictionary<string, long> Seen() =>
        _seen ??= File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? [] : [];
}
```

```csharp
// src/DigitalBrain/Neurons/RollbackDetectedException.cs
namespace DigitalBrain;

// A refusal word: the store served an older version of a part than this machine already saw (Q10).
[GenerateSerializer, Alias("framework.rollback-detected")]
public sealed class RollbackDetectedException(string message) : Exception(message);
```

  `TimeProvider` must resolve in the silo: if LSP shows no registration, add `services.TryAddSingleton(TimeProvider.System);` to `AddDigitalBrainPlatform`.

- [ ] **Step 4: Run** → PASS. **Step 5: Commit** the six files: `git commit -m "Sequencer: signed per-part heads with compare-and-set, and machines refuse rollbacks"`.

### Task 9: Part options and machine services

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartsOptions.cs`
- Modify: `src/DigitalBrain.Platform/DigitalBrain.Platform/PlatformHosting.cs`, `src/Testing/DigitalBrain.Testing.Module/ModuleTest.cs`, `src/Testing/DigitalBrain.Architecture.Tests/ConfigurationKeyOwners.json`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PartsOptionsFacts.cs`

**Interfaces:**
- Produces:
  - `public enum MachineRole { Own = 0, Circle = 1, Cloud = 2 }` (Q24, Q26).
  - `PartsOptions` (key `DigitalBrain:Platform:Parts`): `MachineRole Role = Own`; `string MachineDirectory` (default `%LOCALAPPDATA%/DigitalBrain/machine`); `TimeSpan OwnLease = 7 days`; `TimeSpan ShortLease = 30 s`; `TimeSpan RenewBefore = 10 s` (short) — own leases renew when less than half remains; `int BatchBytes = 262_144`; `TimeSpan BatchDelay = 25 ms`; `int SnapshotEvery = 64`; `TimeSpan Lease => Role == Own ? OwnLease : ShortLease`; `bool AcknowledgeOnJournal => Role == Own`; `Validate()`.
  - Silo singletons `MachineIdentity` (`{MachineDirectory}/identity.key`) and `SeenVersions` (`{MachineDirectory}/seen.json`).

- [ ] **Step 1: Failing tests**

```csharp
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartsOptionsFacts
{
    [Fact]
    public void OwnMachinesHoldLongLeasesAndAcknowledgeOnTheJournal()
    {
        var own = new PartsOptions { Role = MachineRole.Own };
        var cloud = new PartsOptions { Role = MachineRole.Cloud };
        Assert.True(own.Lease > TimeSpan.FromDays(1) && own.AcknowledgeOnJournal);
        Assert.True(cloud.Lease < TimeSpan.FromMinutes(5) && !cloud.AcknowledgeOnJournal);
    }

    [Fact(Timeout = 60_000)]
    public async Task EverySiloHasAMachineIdentityAndRollbackMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        Assert.NotEmpty(brain.SiloServices.GetRequiredService<MachineIdentity>().MachineId);
        Assert.NotNull(brain.SiloServices.GetRequiredService<SeenVersions>());
    }
}
```

- [ ] **Step 2: Run** `... -- --filter-class "*PartsOptionsFacts*"` → FAIL.

- [ ] **Step 3: Implement** `PartsOptions` as listed (shape of `MeshOptions`: `const string Key`, properties, `Validate()` throwing `InvalidOperationException` for non-positive durations or sizes and `RenewBefore >= ShortLease`). Register in `AddDigitalBrainPlatform`:

```csharp
        services.AddOptions<Parts.PartsOptions>().BindConfiguration(Parts.PartsOptions.Key)
            .Validate(parts => { parts.Validate(); return true; }).ValidateOnStart();
        services.TryAddSingleton(provider => Sequencer.MachineIdentity.LoadOrCreate(Path.Combine(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Parts.PartsOptions>>().Value.MachineDirectory, "identity.key")));
        services.TryAddSingleton(provider => new Sequencer.SeenVersions(Path.Combine(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Parts.PartsOptions>>().Value.MachineDirectory, "seen.json")));
```

  In `ModuleTest`, set `DigitalBrain:Platform:Parts:MachineDirectory` to a fresh temp directory per test host. Add `"DigitalBrain.Platform/Parts/PartsOptions.cs"` to `ConfigurationKeyOwners.json`.

- [ ] **Step 4: Run** the Platform suite → PASS. **Step 5: Commit** the five files: `git commit -m "Parts: machine role, leases and batching options; every silo has an identity and rollback memory"`.

---

## Part log (Tasks 10–16)

### Task 10: Log model — one entry is one neuron change (Q27)

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLogModel.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PartLogModelFacts.cs`

**Interfaces:**
- Produces (all `[GenerateSerializer]`, concrete arrays):
  - `enum RecordChange { None = 0, Write = 1, Clear = 2 }` — `None` = an entry that only publishes signals.
  - `LogEntry { long Seq; string GrainId; string StateName; RecordChange Change; byte[] Record; Signal[] Signals; }`
  - `LogSegment { long FirstSeq; LogEntry[] Entries; MeshRef? Prev; }`
  - `PartSnapshot { long Seq; SnapshotRecord[] Records; }`, `SnapshotRecord { string GrainId; string StateName; byte[] Record; long ETagSeq; }`
  - `HeadPayload { MeshRef? Head; MeshRef? Snapshot; long SnapshotSeq; long LastSeq; int SegmentsSinceSnapshot; }`
  - `static class PartSegments { SegmentId(PartId part, long firstSeq) => $"{part.Value}/log/{firstSeq}"; SnapshotId(PartId part, long seq) => $"{part.Value}/snapshot/{seq}"; }`

- [ ] **Step 1: LSP.** `goToDefinition` on `Signal` (`src/DigitalBrain`): confirm it is `[GenerateSerializer]` and polymorphic through Orleans serialization (signals already cross grain calls to observers, so it is). If signal subtypes defined in scripts are not known to the silo's serializer, store `Signals` as `byte[][]` serialized by the publisher's silo and note it here.

- [ ] **Step 2: Failing tests** — round-trip each type through `Orleans.Serialization.Serializer` from `brain.SiloServices`:

```csharp
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartLogModelFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AnEntryCarriesItsRecordAndItsSignalsTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var segment = new LogSegment
        {
            FirstSeq = 5, Prev = new MeshRef("p/log/1", "sha256:aa", ["n0"]),
            Entries = [new LogEntry { Seq = 5, GrainId = "g", StateName = "s", Change = RecordChange.Write, Record = [1], Signals = [new Probed("x")] }],
        };

        var back = serializer.Deserialize<LogSegment>(serializer.SerializeToArray(segment))!;

        Assert.Equal((5L, "p/log/1", RecordChange.Write, new byte[] { 1 }), (back.FirstSeq, back.Prev!.SegmentId, back.Entries[0].Change, back.Entries[0].Record));
        Assert.Equal("x", Assert.IsType<Probed>(Assert.Single(back.Entries[0].Signals)).Value);
    }

    [Fact(Timeout = 60_000)]
    public async Task AHeadPayloadAndASnapshotRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var head = new HeadPayload { Snapshot = new MeshRef("p/snapshot/9", "sha256:bb", ["n1"]), SnapshotSeq = 9, LastSeq = 12, SegmentsSinceSnapshot = 2 };
        var snapshot = new PartSnapshot { Seq = 9, Records = [new SnapshotRecord { GrainId = "g", StateName = "s", Record = [2], ETagSeq = 7 }] };

        var headBack = serializer.Deserialize<HeadPayload>(serializer.SerializeToArray(head))!;
        var snapshotBack = serializer.Deserialize<PartSnapshot>(serializer.SerializeToArray(snapshot))!;

        Assert.Equal((9L, 12L, 2), (headBack.SnapshotSeq, headBack.LastSeq, headBack.SegmentsSinceSnapshot));
        Assert.Equal(7L, Assert.Single(snapshotBack.Records).ETagSeq);
    }
}

[GenerateSerializer, Alias("test.probed")]
public sealed record Probed([property: Id(0)] string Value) : Signal;
```

  (If `Signal` requires constructor arguments, match its shape from Step 1.)

- [ ] **Step 3: Run** `... -- --filter-class "*PartLogModelFacts*"` → FAIL. **Step 4: Implement** the types as listed (aliases `platform.parts.entry`, `.segment`, `.snapshot`, `.snapshot-record`, `.head`, `.record-change`). **Step 5: Run** → PASS. **Step 6: Commit** both files: `git commit -m "Parts: a log entry is one neuron change, its record and its signals"`.

### Task 11: Part lease (Q11, Q15, Q26, Review Focus 1)

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLease.cs`
- Create: `src/DigitalBrain/Neurons/LeaseLostException.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PartLeaseFacts.cs`

**Interfaces:**
- Consumes: `ISequencerGrain`, `HeadRecord`, `MachineIdentity`, `SeenVersions`, `PartsOptions`, `SegmentSeal`, `Serializer`.
- Produces: `internal sealed class PartLease`:
  - `static Task<PartLease> Acquire(PartId part, byte[] key, IGrainFactory grains, MachineIdentity machine, SeenVersions seen, PartsOptions options, TimeProvider time, Serializer serializer)` — refuses the core on a non-own machine (`UnauthorizedAccessException`, Q11); reads the head; `seen.Check`; another machine's live lease → `ServiceUnavailableException`; opens the payload (empty `HeadPayload` when none); CASes itself in as holder with `options.Lease`.
  - `HeadPayload Payload`, `long Version`, `bool NeedsRenewal` (short: less than `RenewBefore` left; own: less than half of `OwnLease` left), `Task Commit(HeadPayload next)` (refused CAS → `LeaseLostException`, and the lease stays lost), `Task Renew()`, `Task Release()`.
  - Payload sealing context: `$"part-head|{part.Value}"`. After each accepted CAS: `seen.Saw`.
  - `public sealed class LeaseLostException(string message) : Exception` in namespace `DigitalBrain`, `[GenerateSerializer, Alias("framework.lease-lost")]`.

- [ ] **Step 1: Failing tests.** Add `Microsoft.Extensions.TimeProvider.Testing` to the test project (`Directory.Packages.props` holds versions; add one there if missing).

```csharp
using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartLeaseFacts
{
    internal static SeenVersions Seen() => new(Path.Combine(Path.GetTempPath(), $"seen-{Guid.NewGuid():N}.json"));

    private static PartsOptions Short => new() { Role = MachineRole.Cloud, ShortLease = TimeSpan.FromSeconds(1), RenewBefore = TimeSpan.FromMilliseconds(500) };

    [Fact(Timeout = 60_000)]
    public async Task ALeaseIsExclusiveUntilItIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var options = new PartsOptions { Role = MachineRole.Own };

        var a = await PartLease.Acquire(part, key, brain.Grains, alice, Seen(), options, TimeProvider.System, serializer);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => PartLease.Acquire(part, key, brain.Grains, bob, Seen(), options, TimeProvider.System, serializer));
        await a.Release();
        Assert.NotNull(await PartLease.Acquire(part, key, brain.Grains, bob, Seen(), options, TimeProvider.System, serializer));
    }

    [Fact(Timeout = 60_000)]
    public async Task ACommitAfterAnotherMachineTookTheLeaseFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await ModuleTest.Create().ConfigureSilo(silo => silo.Services.AddSingleton<TimeProvider>(time)).StartAsync(ct);
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var a = await PartLease.Acquire(part, key, brain.Grains, alice, Seen(), Short, time, serializer);

        time.Advance(TimeSpan.FromSeconds(2));
        var b = await PartLease.Acquire(part, key, brain.Grains, bob, Seen(), Short, time, serializer);

        await Assert.ThrowsAsync<LeaseLostException>(() => a.Commit(new HeadPayload { LastSeq = 99 }));
        await Assert.ThrowsAsync<LeaseLostException>(() => a.Renew());
        Assert.Equal(b.Version, (await brain.Grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf(part.Value)).Get())!.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheCoreIsNeverLeasedToAMachineThatIsNotTheOwners()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var cloud = SequencerFacts.Machine();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PartLease.Acquire(PartId.Core("home"), RandomNumberGenerator.GetBytes(32),
            brain.Grains, cloud, Seen(), new PartsOptions { Role = MachineRole.Cloud }, TimeProvider.System, brain.SiloServices.GetRequiredService<Serializer>()));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOlderHeadThanOnceSeenIsRefusedAtAcquire()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        using var alice = SequencerFacts.Machine();
        var seen = Seen();
        seen.Saw(HeadRecord.HashOf(part.Value), 5);
        await brain.Grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf(part.Value))
            .CompareAndSet(0, SequencerFacts.Record(alice, HeadRecord.HashOf(part.Value), 1, TimeSpan.FromSeconds(-1)));

        await Assert.ThrowsAsync<RollbackDetectedException>(() => PartLease.Acquire(part, RandomNumberGenerator.GetBytes(32), brain.Grains, alice, seen,
            new PartsOptions(), TimeProvider.System, brain.SiloServices.GetRequiredService<Serializer>()));
    }
}
```

- [ ] **Step 2: Run** `... -- --filter-class "*PartLeaseFacts*"` → FAIL.

- [ ] **Step 3: Implement**

```csharp
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// This machine's hold on one part (Q11, Q15, Q26). Every head change is a signed CAS at the sequencer; once a CAS
// is refused the lease is lost for good, so nothing written after it can be reported durable.
internal sealed class PartLease
{
    private readonly PartId _part;
    private readonly byte[] _key;
    private readonly ISequencerGrain _sequencer;
    private readonly MachineIdentity _machine;
    private readonly SeenVersions _seen;
    private readonly PartsOptions _options;
    private readonly TimeProvider _time;
    private readonly Serializer _serializer;
    private DateTimeOffset _expiry;
    private bool _lost;

    private PartLease(PartId part, byte[] key, ISequencerGrain sequencer, MachineIdentity machine, SeenVersions seen,
        PartsOptions options, TimeProvider time, Serializer serializer) =>
        (_part, _key, _sequencer, _machine, _seen, _options, _time, _serializer) = (part, key, sequencer, machine, seen, options, time, serializer);

    public HeadPayload Payload { get; private set; } = new();

    public long Version { get; private set; }

    public bool NeedsRenewal => _expiry - _time.GetUtcNow() < (_options.Role == MachineRole.Own ? _options.OwnLease / 2 : _options.RenewBefore);

    private string Hash => HeadRecord.HashOf(_part.Value);

    private string Context => $"part-head|{_part.Value}";

    public static async Task<PartLease> Acquire(PartId part, byte[] key, IGrainFactory grains, MachineIdentity machine, SeenVersions seen,
        PartsOptions options, TimeProvider time, Serializer serializer)
    {
        if (part.IsCore && options.Role != MachineRole.Own) { throw new UnauthorizedAccessException($"The core of {part.BrainId} is held only by its owner's machines."); }
        var lease = new PartLease(part, key, grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf(part.Value)), machine, seen, options, time, serializer);
        if (await lease._sequencer.Get() is { } current)
        {
            seen.Check(lease.Hash, current.Version);
            if (current.Holder != machine.MachineId && current.LeaseExpiry > time.GetUtcNow())
            { throw new ServiceUnavailableException($"Part {part} is held by machine {current.Holder} until {current.LeaseExpiry:O}."); }
            lease.Version = current.Version;
            lease.Payload = serializer.Deserialize<HeadPayload>(SegmentSeal.Open(key, lease.Context, current.Payload))!;
        }
        await lease.Commit(lease.Payload);
        return lease;
    }

    public async Task Commit(HeadPayload next) => await Cas(next, _time.GetUtcNow() + _options.Lease);

    public Task Renew() => Commit(Payload);

    public async Task Release()
    {
        if (_lost) { return; }
        try { await Cas(Payload, _time.GetUtcNow()); }
        finally { _lost = true; }
    }

    private async Task Cas(HeadPayload next, DateTimeOffset expiry)
    {
        if (_lost) { throw new LeaseLostException($"The lease on part {_part} is lost."); }
        var record = new HeadRecord
        {
            PartHash = Hash, Version = Version + 1, Holder = _machine.MachineId, HolderKey = _machine.PublicKey, LeaseExpiry = expiry,
            Payload = SegmentSeal.Close(_key, Context, _serializer.SerializeToArray(next)),
        };
        record.Signature = _machine.Sign(record.SignedBytes());
        if (!await _sequencer.CompareAndSet(Version, record))
        {
            _lost = true;
            throw new LeaseLostException($"The sequencer refused version {Version + 1} of part {_part}.");
        }
        (Version, Payload, _expiry) = (record.Version, next, expiry);
        _seen.Saw(Hash, Version);
    }
}
```

```csharp
// src/DigitalBrain/Neurons/LeaseLostException.cs
namespace DigitalBrain;

// A refusal word: this machine no longer holds the part it was writing; nothing after this is durable.
[GenerateSerializer, Alias("framework.lease-lost")]
public sealed class LeaseLostException(string message) : Exception(message);
```

- [ ] **Step 4: Run** → PASS. **Step 5: Commit** the three files: `git commit -m "Parts: a part lease is a chain of signed CASes, long on own machines, never the core elsewhere"`.

### Task 12: Local journal and the part log (Q18, Q26, Review Focus 2–3)

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartJournal.cs`
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLog.cs`
- Create: `src/DigitalBrain/Neurons/JournalConflictException.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PartJournalFacts.cs`, `PartLogFacts.cs`

**Interfaces:**
- Produces:
  - `internal sealed class PartJournal(string path, byte[] key, Serializer serializer)`: `void Append(LogEntry entry)` — seals the entry (`SegmentSeal`, context `part-journal|{path}`), writes `[int32 length][sealed]`, flushes to disk (`FileStream.Flush(flushToDisk: true)`); `LogEntry[] Read()` — every entry, in order (a torn last record is ignored); `void TrimThrough(long seq)` — rewrites the file without entries `<= seq` (temp file + atomic move); `void SetAside()` — renames the file to `{path}.conflict-{utc:yyyyMMddHHmmss}`.
  - `internal sealed class PartLog : IAsyncDisposable`:
    - `static Task<PartLog> Open(PartId part, byte[] key, PartLease lease, IMeshStore mesh, Serializer serializer, PartsOptions options, TimeProvider time, CancellationToken ct)` — loads the snapshot, replays the chain (walk `Prev` from `Head` to `SnapshotSeq`, apply oldest first) into `Dictionary<(string GrainId, string StateName), StoredRecord>` (`StoredRecord(byte[] Record, long ETagSeq)`); keeps every replayed entry with signals in `List<LogEntry> Since` (entries after the snapshot). On an own machine, then reads `{MachineDirectory}/journal/{HeadRecord.HashOf(part)}.log`: entries with `Seq <= LastSeq` are trimmed; entries continuing at `LastSeq + 1` are applied and queued for commit; entries whose first seq is not `LastSeq + 1` mean another machine wrote meanwhile → `SetAside()` and throw `JournalConflictException`.
    - `StoredRecord? Read(string grainId, string stateName)`; `long LastSeq`.
    - `Task<long> Append(string grainId, string stateName, RecordChange change, byte[] record, Signal[] signals)` — assigns the next seq; on an own machine journals first and returns once journaled (`AcknowledgeOnJournal`), with the commit following in the background; elsewhere returns once committed.
    - `IReadOnlyList<LogEntry> SignalsSince(long seq)` — entries after `seq` that carry signals, from `Since` (Task 16 extends this past snapshots).
  - Group commit: the first append to an empty queue schedules a flush after `BatchDelay`; reaching `BatchBytes` flushes at once. A flush: `mesh.Write` one `LogSegment { Prev = Payload.Head }`, `lease.Commit(new HeadPayload { Head = ref, …, LastSeq, SegmentsSinceSnapshot + 1 })`, then `journal.TrimThrough(LastSeq)` and complete each waiter. Flushes are serialized by a `SemaphoreSlim(1)`. A failure other than `LeaseLostException` leaves the batch queued (own machine: still journaled, retried on the next flush with backoff 1 s → 60 s; elsewhere: waiters fail and the queue is cleared, `_nextSeq` reset to `LastSeq + 1`). `LeaseLostException` closes the log: every waiter and later `Append` fails with it. A renew loop calls `lease.Renew()` when `NeedsRenewal` and no flush is pending.
  - `public sealed class JournalConflictException(string message) : Exception` in namespace `DigitalBrain`, `[GenerateSerializer, Alias("framework.journal-conflict")]`.

- [ ] **Step 1: Failing journal tests**

```csharp
using System.Security.Cryptography;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartJournalFacts
{
    [Fact(Timeout = 60_000)]
    public async Task EntriesSurviveReopeningAndATornTailIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.log");
        var journal = new PartJournal(path, key, serializer);
        journal.Append(new LogEntry { Seq = 1, GrainId = "g", StateName = "s", Change = RecordChange.Write, Record = [1], Signals = [] });
        journal.Append(new LogEntry { Seq = 2, GrainId = "g", StateName = "s", Change = RecordChange.Write, Record = [2], Signals = [] });
        await File.AppendAllBytesAsync(path, [0, 0, 1, 0, 42], ct);

        var entries = new PartJournal(path, key, serializer).Read();

        Assert.Equal([1L, 2L], entries.Select(e => e.Seq));
        Assert.DoesNotContain("g", await File.ReadAllTextAsync(path, ct), StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task TrimmingDropsCommittedEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var journal = new PartJournal(Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.log"), RandomNumberGenerator.GetBytes(32), brain.SiloServices.GetRequiredService<Serializer>());
        foreach (var seq in new long[] { 1, 2, 3 }) { journal.Append(new LogEntry { Seq = seq, GrainId = "g", StateName = "s", Record = [], Signals = [] }); }

        journal.TrimThrough(2);

        Assert.Equal([3L], journal.Read().Select(e => e.Seq));
    }
}
```

- [ ] **Step 2: Failing log tests** in `PartLogFacts`, with this helper (the test host registers 8 storage nodes, owner `platform`, and `MeshOptions` 4+2 — Task 13 moves that into `ModuleTest`; until then the helper does it as `MeshStoreFacts.StartAsync` does):

```csharp
    internal static async Task<PartLog> OpenAsync(ModuleBrain brain, PartId part, byte[] key, MachineIdentity machine, PartsOptions options, TimeProvider time, CancellationToken ct)
    {
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var lease = await PartLease.Acquire(part, key, brain.Grains, machine, PartLeaseFacts.Seen(), options, time, serializer);
        return await PartLog.Open(part, key, lease, brain.SiloServices.GetRequiredService<IMeshStore>(), serializer, options, time, ct);
    }
```

  Facts (each a full test body in the file):
  - `AppendedChangesAreReadBackAfterReopening` — Cloud role; two writes to `("g1","s")`; dispose (releases); reopen; `Read` returns the second record with `ETagSeq == 2`.
  - `ConcurrentAppendsShareOneSegment` — Cloud role, `BatchDelay = 50 ms`; 20 appends started together; seqs are 1..20; head payload `SegmentsSinceSnapshot == 1`.
  - `ASegmentWithoutAnAcceptedHeadIsInvisible` (Review Focus 3) — one committed write; then `IMeshStore.Write` a segment with `Prev = lease.Payload.Head` and no CAS; reopen; `LastSeq == 1` and the extra change is absent.
  - `AnOwnMachineKeepsWritingWhileTheSequencerIsUnreachableAndCommitsOnReconnect` — Own role; simulate the outage with a test `IMeshStore` decorator that throws `ServiceUnavailableException` while `Offline = true` (a flush fails at the mesh write, before the CAS, exactly as an unreachable platform would); two appends return (journaled); `Read` sees them; `Offline = false`; wait for the flush (`await log.Flushed()` — expose `internal Task Flushed()` that completes when the queue is empty); reopen on the same machine: both changes present, journal file empty.
  - `AnOwnMachinesJournalThatConflictsIsSetAsideAndRefused` (Review Focus 2) — Own machine A journals one change while offline, then its lease is expired with `FakeTimeProvider` and machine B (Own role, same account) acquires and commits one change; reopening A's log throws `JournalConflictException` and a `*.conflict-*` file exists next to the journal.
  - `AWriteAfterTheLeaseIsLostFails` — Cloud role; B takes the lease after expiry; A's next `Append` throws `LeaseLostException`; B's reopened log shows only A's committed changes.

- [ ] **Step 3: Run** `... -- --filter-class "*PartJournalFacts*|*PartLogFacts*"` (run them as two commands if the filter syntax refuses `|`) → FAIL.

- [ ] **Step 4: Implement** `JournalConflictException`, `PartJournal` and `PartLog` per the interfaces. The heart of the flush:

```csharp
    private async Task Flush()
    {
        await _flushing.WaitAsync();
        try
        {
            Pending[] batch;
            lock (_queue) { batch = [.. _queue]; }
            if (batch.Length == 0) { return; }
            var payload = _lease.Payload;
            var segment = new LogSegment { FirstSeq = batch[0].Entry.Seq, Entries = [.. batch.Select(p => p.Entry)], Prev = payload.Head };
            try
            {
                var reference = await _mesh.Write(_key, PartSegments.SegmentId(_part, segment.FirstSeq), _serializer.SerializeToArray(segment));
                await _lease.Commit(new HeadPayload
                {
                    Head = reference, Snapshot = payload.Snapshot, SnapshotSeq = payload.SnapshotSeq,
                    LastSeq = batch[^1].Entry.Seq, SegmentsSinceSnapshot = payload.SegmentsSinceSnapshot + 1,
                });
                _chain.Add(reference);
                lock (_queue) { _queue.RemoveRange(0, batch.Length); }
                _journal?.TrimThrough(batch[^1].Entry.Seq);
                foreach (var pending in batch) { pending.Committed.TrySetResult(pending.Entry.Seq); }
                _backoff = TimeSpan.FromSeconds(1);
            }
            catch (LeaseLostException lost)
            {
                _closed = lost;
                FailAll(lost);
            }
            catch (Exception error) when (_journal is not null)
            {
                _logger.LogWarning(error, "Part {Part} could not commit; {Count} journaled changes wait for the next attempt.", _part, batch.Length);
                ScheduleFlush(_backoff);
                _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
            }
            catch (Exception error)
            {
                FailAll(error);
                _nextSeq = _lease.Payload.LastSeq + 1;
            }
        }
        finally { _flushing.Release(); }
    }
```

  (`Pending` = `(LogEntry Entry, TaskCompletionSource<long> Committed)`; `Append` applies the change to the in-memory map immediately on an own machine after journaling, and after commit elsewhere; `_chain` holds the refs of the current chain for Task 15's discards; `ILogger<PartLog>` is passed to `Open`.)

- [ ] **Step 5: Run** → PASS (all eight facts). **Step 6: Commit** the five files: `git commit -m "Parts: the part log replays from its head, group-commits changes, and own machines journal through outages"`.

### Task 13: On a machine, `cluster` storage is the part log

**Files:**
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLogs.cs`
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartGrainStorage.cs`
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/MachineHosting.cs`
- Modify: `src/Testing/DigitalBrain.Testing.Module/ModuleTestBuilder.cs` (`WithPartLog()`), `ModuleTest.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PartGrainStorageFacts.cs`

**Interfaces:**
- Consumes: `PartId.Of` (Task 2), `IPartKeys`, `PartLease`, `PartLog`.
- Produces:
  - `internal sealed class PartLogs(IGrainFactory grains, IMeshStore mesh, IPartKeys keys, MachineIdentity machine, SeenVersions seen, Serializer serializer, IOptions<PartsOptions> options, TimeProvider time, ILoggerFactory logging) : IAsyncDisposable` with `Task<PartLog> For(PartId part)` — one `PartLog` per part (`ConcurrentDictionary<string, Lazy<Task<PartLog>>>`), reopened after `LeaseLostException`; the owner account comes from the brain: `grains.GetGrain<IBrain>(part.BrainId).Read()` → `Owner.AccountId` (LSP-check `IBrain.Read` and `Owner`), cached per brain; `DisposeAsync` releases every lease.
  - `internal sealed class PartGrainStorage(PartLogs logs, Serializer serializer) : IGrainStorage` — ETag = entry seq; a write whose `ETag` differs from the stored record's → `InconsistentStateException`; signals pending for this write (Task 14) ride in the same entry.
  - `public static ISiloBuilder AddMachinePartLog(this ISiloBuilder silo)` in `MachineHosting` — registers `PartLogs` and replaces the keyed `IGrainStorage` for `ClusterGrainStorage` with `PartGrainStorage`, *before* the kernel registration decorator wraps it (LSP `goToDefinition` on `InstanceRegistration.TryDecorate` and call it after replacing).
  - `ModuleTestBuilder.WithPartLog()` — calls `AddMachinePartLog`, registers 8 seed nodes with owner `platform` in `IStorageNodeDirectory`, and sets `MeshOptions` to 4+2 with 2 manifest replicas. Plain module tests keep memory `cluster` storage.

- [ ] **Step 1: Failing tests** — a test neuron in the test project:

```csharp
public interface IPartCounter : INeuron { Task<int> Increment(); Task<int> Read(); }

[GrainType("test.part-counter")]
public sealed class PartCounterNeuron([PersistentState("counter", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state) : Neuron, IPartCounter
{
    public async Task<int> Increment() { state.State.Value++; await state.WriteStateAsync(); return state.State.Value; }
    public Task<int> Read() => Task.FromResult(state.State.Value);
}

[GenerateSerializer, Alias("test.part-counter-state")]
public sealed class PartCounterState { [Id(0)] public int Value { get; set; } }
```

  Facts (keys `{scope}/apps/counter/c1`, where `scope = BrainScope.Create(account, "home").Id` and the brain is created first through `brain.Self`):
  - `ANeuronKeepsItsStateInItsPartAcrossASiloRestart` — increment 3×; `brain.RestartSiloAsync`; `Read()` == 3; the sequencer has a head for `HeadRecord.HashOf("{scope}/apps/counter")`.
  - `NoNeuronStateReachesPlatformStorage` — after an increment, the keyed `IGrainStorage` for `PlatformGrainStorage` has no record for the counter grain (`ReadStateAsync` → `RecordExists == false`).
  - `AStaleETagIsRefused` — call `PartGrainStorage.WriteStateAsync` directly with a `GrainState<PartCounterState>` whose `ETag` is `"0"` after a write moved it on → `InconsistentStateException`.
  - `AKeyOutsideEveryBrainIsRefusedOnAMachine` — a test grain keyed `@platform/x` on `ClusterGrainStorage` fails its first write with `ArgumentException` (Q27: a machine hosts only brain parts).

- [ ] **Step 2: Run** `... -- --filter-class "*PartGrainStorageFacts*"` → FAIL.

- [ ] **Step 3: Implement.** `PartGrainStorage.ReadStateAsync`:

```csharp
    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var log = await logs.For(PartId.Of(grainId.Key.ToString()!));
        if (log.Read(grainId.ToString(), stateName) is { } stored)
        {
            grainState.State = serializer.Deserialize<T>(stored.Record)!;
            (grainState.ETag, grainState.RecordExists) = (stored.ETagSeq.ToString(CultureInfo.InvariantCulture), true);
        }
        else
        {
            grainState.State = Activator.CreateInstance<T>();
            (grainState.ETag, grainState.RecordExists) = (null, false);
        }
    }
```

  `WriteStateAsync` checks the ETag against `log.Read(...)?.ETagSeq`, then `await log.Append(grainId.ToString(), stateName, RecordChange.Write, serializer.SerializeToArray(grainState.State), SignalJournal.TakePending())` and sets `ETag` to the returned seq and `RecordExists = true`. `ClearStateAsync` appends `RecordChange.Clear` the same way. (`SignalJournal.TakePending()` arrives in Task 14; until then pass `[]` and replace it there.)

- [ ] **Step 4: Run** the Platform suite, then **every module's unit suite once with `WithPartLog()` forced on** (temporarily flip the `ModuleTest` default in a scratch run, do not commit the flip) to prove neurons run unchanged on the part log; fix readiness problems it exposes, never timeouts.

- [ ] **Step 5: Commit** the six files: `git commit -m "Parts: on a machine, cluster grain storage is the part log; neurons are unchanged"`.

### Task 14: A state write and its signals are one entry (Q27, Review Focus 4)

**Files:**
- Create: `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Signalling/SignalJournal.cs`
- Modify: `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Neuron.cs`
- Create: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartSignalJournal.cs`
- Modify: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartGrainStorage.cs`, `MachineHosting.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PartSignalFacts.cs`

**Interfaces:**
- Produces (Framework):

```csharp
namespace DigitalBrain.Framework;

// Where a neuron's signals become durable before anyone sees them (Q27). Hosts without a part log journal
// nothing; on a machine, signals ride in the same log entry as the state write that caused them, or alone.
public interface ISignalJournal
{
    // A signal published without a state write: durable once this completes.
    Task Append(GrainId publisher, Signal[] signals);
}

public static class SignalJournal
{
    private static readonly AsyncLocal<List<Signal>?> Pending = new();

    // Signals a Save is about to publish; the storage write running inside this scope takes them.
    public static IDisposable Attach(Signal[] signals)
    {
        var previous = Pending.Value;
        Pending.Value = [.. signals];
        return new Scope(previous);
    }

    public static Signal[] TakePending()
    {
        if (Pending.Value is not { } pending) { return []; }
        Signal[] taken = [.. pending];
        pending.Clear();
        return taken;
    }

    internal static bool Taken => Pending.Value is { Count: 0 };

    private sealed class Scope(List<Signal>? previous) : IDisposable
    {
        public void Dispose() => Pending.Value = previous;
    }
}

internal sealed class NoSignalJournal : ISignalJournal
{
    public Task Append(GrainId publisher, Signal[] signals) => Task.CompletedTask;
}
```

- `Neuron` changes (LSP `findReferences` on `PublishAsync` first — 58 callers stay source-compatible):
  - split stamping from notifying: `private Signal Stamp(Signal signal) => signal with { Publisher = Id, Trace = TraceContext.Current };` and `private Task Notify(Signal stamped)` (the current hub publish + observers notify).
  - `protected async Task PublishAsync(Signal signal) { var stamped = Stamp(signal); await Journal.Append(this.GetGrainId(), [stamped]); await Notify(stamped); }` where `Journal` resolves `ISignalJournal` from `ServiceProvider` (registered `NoSignalJournal` by default in the Framework's silo registration — LSP-find where `LocalSignalHub` is registered and add it beside).
  - `Neuron<TState>.Save(next, changed, acted)`: stamp both signals; `using (SignalJournal.Attach(acted is null ? [c] : [c, a])) { await SaveQuietly(next); }`; if the storage took them (`SignalJournal.Taken` read inside the scope before disposing), notify without journaling again; otherwise (memory storage in plain tests) journal them through `Journal.Append` as today's `PublishAsync` would. Then `Notify` each.
  - `Clear(changed)`: same pattern around `store.ClearStateAsync()`.
- Platform: `PartSignalJournal(PartLogs logs) : ISignalJournal` — `Append` = `log.Append(publisher.ToString(), "", RecordChange.None, [], signals)`; `AddMachinePartLog` registers it in place of `NoSignalJournal`. `PartGrainStorage` passes `SignalJournal.TakePending()` (replacing Task 13's `[]`).

- [ ] **Step 1: Failing tests** (`WithPartLog()` host; a test neuron deriving `Neuron<TState>` whose `Change(int)` calls `Save(next, new Changed(value))`, and whose `Ping()` calls `PublishAsync(new Pinged())`):
  - `AStateChangeAndItsSignalAreOneEntry` — after `Change(5)`, the part log's last entry has `Change == Write` and one `Changed` signal.
  - `ASignalWithoutAStateChangeIsItsOwnEntry` — after `Ping()`, the last entry has `Change == None` and one `Pinged`.
  - `AFailedStateWriteNeverPublishesItsSignal` — make the part log refuse the write (lose the lease through `FakeTimeProvider` and a second machine); a subscriber (`brain.SubscribeAsync<Changed>`) receives nothing and `Change` throws `LeaseLostException`.
  - `PlainHostsStillPublishAsBefore` — without `WithPartLog()`, `Change(1)` delivers `Changed` to a subscriber (regression for every module test).

- [ ] **Step 2: Run** `... -- --filter-class "*PartSignalFacts*"` → FAIL. **Step 3: Implement.** **Step 4: Run** the Platform, Framework and Client suites, and one module suite per ring touched (Apps, Flutter, AI) → PASS. **Step 5: Commit** the six files: `git commit -m "Signals: a neuron's signals are durable in its part log, in the same entry as the change that caused them"`.

### Task 15: Snapshots and garbage

**Files:**
- Modify: `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartLog.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PartSnapshotFacts.cs`

**Interfaces:**
- Produces: `Task Snapshot()` on `PartLog`, run by a flush when `SegmentsSinceSnapshot >= SnapshotEvery` (inside the flush lock, after completing the batch): `mesh.Write(PartSnapshot { Seq = LastSeq, Records = map })` as `SnapshotId(part, LastSeq)`; `lease.Commit(new HeadPayload { Head = null, Snapshot = ref, SnapshotSeq = LastSeq, LastSeq, SegmentsSinceSnapshot = 0 })`; then `mesh.Discard` the previous snapshot and every ref in `_chain` (collected during `Open` and each flush, so no reads are needed). Discard failures are logged, never thrown. Signals in the discarded chain stay readable for subscribers through `Since` (in memory) until Task 16 decides retention; a snapshot keeps the signals of the last `SnapshotEvery` segments in `PartSnapshot.RecentSignals` (`LogEntry[]`, add the field) so a reopened log can still serve them.

- [ ] **Step 1: Failing tests:**
  - `ASnapshotReplacesTheChainAndReopeningReadsIt` (`SnapshotEvery = 2`, three single-change flushes, reopen: state intact; head payload `Snapshot != null`, `SegmentsSinceSnapshot == 1`).
  - `ReplacedSegmentsAreDiscarded` (refs of the first two segments read from the head chain before the snapshot; after it, `IMeshStore.Read` of each throws `ServiceUnavailableException`).
  - `SignalsBeforeASnapshotAreStillServedAfterReopening` (`SignalsSince(0)` after reopen contains the pre-snapshot signals).

- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. **Step 5: Commit**: `git commit -m "Parts: periodic snapshots replace the chain, and the writer discards what they replaced"`.

### Task 16: Subscriptions read the part log from their watermark (Q13)

The signal path is now durable (Task 14); this task makes `brain.On<T>(source)` replay from it after reactivation instead of only receiving live notifications.

- [x] **Step 1: Map with LSP.** Result:
  - `brain.On<T>(source)` in a behavior runs over `SessionSignalSubscription<T>` (Client) → `SessionConnection.SubscribeAsync` → `IBrainRuntimeEdge.Subscribed` on `BrainRuntimeNeuron` (`src/DigitalBrain.Modules/Microsoft/CSharp/DigitalBrain.Modules.Microsoft.CSharp/Runtime/BrainRuntimeNeuron.cs`). The durable subscription is the `Subscription` record (`Runtime/Subscription.cs`) in `BrainRuntimeState.Subscriptions`, persisted in the runtime neuron's own state. Its `Watermark` (`Delivered + Pending.Length`) is the sandbox's ack counter, not a source position; live signals reach it through `INeuronObserver.OnSignalAsync` → one-way `IBrainRuntimeArrivals.Arrived`; `ResumeAsync` (from `BrainRuntimeActivation`) re-watches sources on activation.
  - `IBrainSubscriptions.SubscribeAsync` / `ISignalSubscription.ReadAllAsync` / `SignalSubscription` (Client.Orleans `BrainClient`, `ModuleBrain`, `E2EBrain`, `ObservedBrain`) are silo/test live handles with no durable watermark; they stay live-only.
  - Signal durability: `ISignalJournal` (Framework `Runtime/Signalling/SignalJournal.cs`, `NoSignalJournal` registered by `BrainHosting`), `PartSignalJournal` (Platform, swapped in by `MachineHosting.AddMachinePartLog`), `PartLog.SignalsSince`, `PartSnapshot.RecentSignals`; test double `CloudPartJournal` in `PartSignalFacts.cs`.
  - **Watermark type:** `long SourceSeq` on `Subscription`, the part-log seq of the last entry of the source's part it has buffered. It lives in the runtime neuron's state (state lives in neurons; `brain.On<T>` unchanged). Signals of one entry share its seq, so dedup is by entry seq.
- [x] **Step 2: Step-level tasks** (requirements: watermark is a part-log seq; (re)activation reads the source's part from it before live delivery, dedup by seq; a part not leasable here fails with `ServiceUnavailableException`; `brain.On<T>` unchanged; plain hosts unchanged).

#### Task 16.1: The journal reads one publisher's signals from a part-log seq

**Files:**
- Modify: `src/DigitalBrain.Framework/DigitalBrain.Framework/Runtime/Signalling/SignalJournal.cs`, `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartSignalJournal.cs`, `.../Parts/PartLog.cs`, `.../Parts/PartLogModel.cs`
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/PublisherSignalsFacts.cs` (new); `PartSignalFacts.cs` (`CloudPartJournal` gains the member)

**Interfaces:**
- `Task<PartSignals?> ISignalJournal.SignalsOf(GrainId publisher, long since)`; `record PartSignals(long Floor, long Last, JournaledSignal[] Signals)`: `JournaledSignal.Sequence` is the entry's part-log seq; `Last` is the log's `LastSeq` (a new subscription's starting watermark); entries at or below `Floor` may no longer be served. `NoSignalJournal` returns `null` (nothing to replay).
- `PartSignalJournal.SignalsOf` opens the publisher's part through `PartLogs.For` (a part leased by another machine throws `ServiceUnavailableException` from `PartLease.Acquire`).
- Retention: `PartSnapshot.SignalsFrom` (the previous snapshot seq, below which `RecentSignals` is incomplete), `PartLog.SignalsFloor` set from it on replay (0 for a log never snapshotted).

- [ ] **Step 1: Failing facts:** `APublishersSignalsAreReadAfterASeqWithTheirEntrySeq`, `APartHeldByAnotherMachineIsUnavailable`, `ASnapshotRaisesTheFloorAReopenedLogServesFrom`, `PlainHostsHaveNothingToReplay`.
- [ ] **Step 2: Run** `dotnet test --project src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit -- --filter-class "*PublisherSignalsFacts*"` → FAIL (compile). **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit:** `Signals: the journal reads one publisher's signals from a part-log seq, with the floor its log still serves`.

#### Task 16.2: A runtime subscription's watermark is its source's part-log seq

**Files:**
- Modify: `.../CSharp/DigitalBrain.Modules.Microsoft.CSharp/Runtime/Subscription.cs`, `Runtime/BrainRuntimeNeuron.cs`
- Test: `src/DigitalBrain.Modules/Microsoft/CSharp/tests/DigitalBrain.Modules.Microsoft.CSharp.Tests/PartLogReplayFacts.cs` (new)

**Interfaces:** `Subscription.SourceSeq`; a new subscription starts at `PartSignals.Last`; on a journaled host a live arrival is a nudge: the runtime reads `SignalsOf(source, min SourceSeq)` and buffers each signal with seq above a subscription's `SourceSeq`, then advances it. `null` from the journal keeps today's path (buffer the live signal).

- [ ] **Step 1: Failing facts:** `ANewSubscriptionStartsAtItsSourcesCurrentSeq` (a signal journalled before subscribing is not replayed), `ASignalJournalledWithoutANotificationArrivesWithTheNextLiveOneOnce` (entry appended straight to the part log, then a live ping: pending holds each once, in seq order).
- [ ] **Step 2: Run** `... -- --filter-class "*PartLogReplayFacts*"` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit:** `CSharp: a runtime subscription's watermark is its source's part-log seq, and live arrivals deliver from the log once`.

#### Task 16.3: Reactivation catches up from the watermark; an unleasable source is unavailable

**Files:** `Runtime/BrainRuntimeNeuron.cs`, `Runtime/IBrainRuntimeArrivals.cs`; test `PartLogReplayFacts.cs`.

**Interfaces:** `[OneWay] Task IBrainRuntimeArrivals.CatchUp()` sent by `ResumeAsync` (never reads a log inside activation); a re-subscribing behavior (`Subscribed` on an existing subscription) catches up before its batch is pushed, so `ServiceUnavailableException` from the journal reaches the sandbox; a read whose `Floor` is above a watermark logs `SubscriptionGap` and continues from what is served.

- [ ] **Step 1: Failing facts:** `SignalsJournalledWhileTheRuntimeSleptAreDeliveredWhenItReactivates`, `AReactivatedRuntimeDoesNotRedeliverWhatItBuffered`, `ASourceWhosePartIsNotLeasedHereFailsTheSubscribeAsUnavailable` (journal double throwing `ServiceUnavailableException`).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** the Platform, Framework, CSharp and Apps suites → PASS.
- [ ] **Step 5: Commit:** `CSharp: a reactivated runtime reads its sources' part logs from each watermark before live signals`.

`GET /brains/{id}/signals` (`ISignalJournal.ReadSince`, commit 6bc6cd2ff) keeps its per-read rank: its durable cursor is a `(part, seq)` vector, which Task 16 defines per source part; re-basing the endpoint on it is left to the shell's signals view.

### Stage A acceptance (live)

- [ ] A Platform startup task registers `DigitalBrain:Platform:Mesh:SeedNodes` (count, default 0) seed nodes with owner `platform` (Q25); the AppHost sets it to 24 and calls `AddMachinePartLog` on the OS silo with `Role = Own`.
- [ ] `dotnet run digitalbrain.cs`; `mcp__aspire__list_resources` all Running/Healthy (read `list_console_logs` for any that is not, before anything else).
- [ ] Through `digitalbrain-mcp`: `neurons_context`; create an app; `neurons_call` a mutation on one of its neurons; restart the runtime resource (`mcp__aspire__execute_resource_command`); `neurons_get` shows the state.
- [ ] Stop Azurite's sequencer path is not possible in one process, so check Q26 with the unit fact; live, check that `list_traces` shows mesh writes and one sequencer CAS per flush, and `list_structured_logs` shows no `LeaseLost`, `RollbackDetected` or `JournalConflict`.
- [ ] Azurite holds no brain-scoped neuron state, only `platform` grains.

---

# Stage B — split and connect (first release)

Each plan opens with a gate task that writes its step-level tasks into this file (Stage A's format) after the measurement; interfaces and acceptance below are fixed.

## Plan 5 — Trust cluster and machine hosts (Tasks 17–19)

### Task 17 (gate): host shapes

**Measured** (2026-10-10, HEAD 8c5677e48, in-process `InProcessTestCluster`, Debug, one test process; a scratch timing fact under `DigitalBrain.Platform.Tests.Unit` run in three invocations, each starting every shape twice; deleted after measuring because it asserts nothing). "Cold" = first silo in the process (JIT, Orleans type manifest and grain-type discovery); "warm" = second silo in the same process.

| Shape | Cold (median; 3 runs) | Warm (median) | Dominated by |
|---|---|---|---|
| Trust only: bare silo, memory `platform` storage, no `AddDigitalBrain`, no modules, no neurons — deploy | 0.70 s (0.69 / 0.70 / 0.73) | 0.11 s | Orleans silo start + first-time type discovery |
| …then first sequencer `Get`, keyring `Get`, 24 seed-node `Register`s | 0.12 s | 0.01 s | first activations (memory writes) |
| Module host (`ModuleTest.Create()`: kernel + platform + client, memory `cluster` and `platform`) | 0.18 s* | 0.12 s | silo start; the trust registrations are not separable inside `AddDigitalBrainPlatform` |
| Machine host (`WithPartLog()`: part log, 8 seed nodes, owner provisioned, core part opened = lease + first CAS + first mesh write) | 0.21 s* | 0.15 s | the part log adds ~0.04 s over the module host |

\* started after the trust-only silo in the same process, so JIT was already paid; a true cold figure is ~0.6 s more. Azure storage was **not** measured in-process (no Azurite fixture in the Platform unit suite; the AppHost was off-limits); the memory rows are a stated **lower bound** for the trust cluster. Live context from Stage A: the OS silo restarted in ~20 s — the OS modules and the features catalog, not the trust grains or the part log.

**What the numbers show, and what they do not.** They show only that the split costs nothing at start: trust grains activate lazily (24 seed registrations ≈ 10 ms warm) and a part opens in ~40 ms. H1 and H2 do **not** rest on these numbers; they rest on lease semantics (one holder per part, days-long own leases) and on Orleans' single-activation semantics.

**Rulings (bind Tasks 18–19):**
- **H1 — a machine is one silo (Q12), in Stage B too.** No shared identity, no part-pinned placement. Two silos of one machine would need an exclusive cross-process journal writer and a placement director that duplicates the lease; a roll is a restart the journal covers (Q26). Scale-out is more machines leasing different parts. R23 is permanent.
- **H2 — machines and the trust host are separate Orleans clusters**, never one cluster with a placement strategy: inside one cluster the single-activation directory makes "B acquires A's part" meaningless and lets a machine address trust grains around the client seam. How a machine reaches the trust cluster is R34 (Task 18 Step 6).
- **H3 — Task 18's handover part is an app part** (`{scope}/apps/counter`), never the core: kernel instance records (`InstanceDirectoryGrain` and the rest of `KernelGrains.cs`) live in the core part, which only an own machine holds (Q11).
- **H4 → R31/R37/R44 — the Marketplace catalog stays trust-side (R14).** `IMarketplace : INeuron` stays in `DigitalBrain.Modules.Apps.Contracts` (scripts and clients use it; `AppNeuron` and `MarketplaceView` keep calling it). The Apps module's `MarketplaceNeuron` becomes a **stateless** neuron (no grain storage; `MarketplaceNeuron.OwnerKey` is deleted) that delegates each of `List`, `Read`, `Upsert`, `Remove` to `IMarketplaceClient`, a Framework platform-facing interface; Platform implements the client over the gateway; `MarketplaceGrain` in `DigitalBrain.Platform.Trust` (`platform` storage) holds the catalog. `MarketplaceWriteGuard` changes (R44): its Remove path reads ownership through `await IMarketplaceClient.Read(publisher, name)` instead of the neuron's state. `Apps.Contracts` references only `src/DigitalBrain` (not Framework), so `MarketplaceListing` stays in Apps.Contracts and the neuron maps it to Framework's `MarketplaceRecord`. The `StorageRingFacts.TrustClusterNeurons` entry is removed. No platform brain, no platform machine.
- **R37/R41 — caller stamping across the gateway.** Every trust call that acts for an account carries the kernel-stamped caller account as an explicit argument inside a `MachineSigned<T>` payload (payload, account, machine id, timestamp, signature by the machine identity key). The trust side verifies the signature against the key the node directory recorded for that machine id, and honours the payload's account only if it equals the machine's **registered account** (R41); otherwise `UnauthorizedAccessException`.
- **R41/R50 — machine registration.** `INodeDirectoryClient.RegisterMachine(machineId, publicKey, account, selfSignature)`: there is no cross-host identity token in Stage B (R50). The trust side verifies `selfSignature`, records `machineId → (publicKey, account)` in the node directory — trust on first use for both the key and the account: the account binds to the first machine that registers it; a different key for the same machine id is refused, and a second machine claiming an already-bound account is refused until a pairing flow exists (Task 21's handshake or owner approval, outside this release). Known cost: whoever registers an unbound account first owns it — mitigated by seed deployments registering `platform` and the owner's machines first, and by Task 19 attestation. LSP confirms the other `*Neuron`-named platform types (`SecretsNeuron`, `IntegrationNeuron`, `ConnectionIndexNeuron`, `WorkshopNeuron`, `ConnectionRequestsNeuron`) derive from `Grain`; all stay machine-side (R38).
- **H5 → R36 — owner lookup.** `IKeyringClient.OwnerOf(brainId)` calls `IBrainAuthority.Account()` on the trust side (R13 primary); the keyring holds no owner record.
- **H6 — custody is a Framework contract.** `IMasterKeyCustody` moves to Framework (Task 19), with `IMachineSigningKey` beside it (R35).
- **R30/R42/R43 — no credential reaches the trust host.** Custody stays **machine-side**: `AddMasterKeyWrapper` and `IMasterKeyCustody` are machine registrations, never trust ones. User connection secrets live in the machine-side Secrets grain (R38), sealed under the owner's custody as today; `ISecrets` keeps its current signature (R43). Deployment credentials are machine-host configuration (`DigitalBrain:Integrations:*`), seeded by the machine-side `IntegrationSeeder` into the machine-side Integration grain (R42). The trust host receives no credential of any kind — no secret, no integration credential, no key other than wrapped part keys and machine public keys.
- **Visibility.** The four clients are `internal` to `DigitalBrain.Platform`, which already grants `InternalsVisibleTo` to `DigitalBrain.Testing.Module`; `DigitalBrain.Platform.Trust` grants the same to `DigitalBrain.Platform`, `DigitalBrain.Testing.Module`, `DigitalBrain.Platform.Tests.Unit` and `DigitalBrain.Trust.Tests.Unit`.

**Carried from Stage A (Task 18 absorbs):** R16 — ~430 module facts write into never-created brains and fail when the part log is forced on; Task 18 Step 8 provisions the default brain on every `ModuleTest` host. R25 — `InstanceDirectoryGrain` rides the core part on `cluster` storage and sends instance signals one-way after its write; Step 8 records R25 as still parked in the ledger by default (moving it onto `ISignalJournal` is done only if it is one step).

### Task 18: Two hosts

**Breaker rulings (2026-10-10, override R50/R51 and the registration site where they conflict):**
- **R52 — pairing secret (replaces R50's single-machine binding).** An account may bind several machines when they share a pairing secret: `DigitalBrain:Platform:Parts:PairingSecret` (machine-side configuration; the AppHost sets one value for every OS silo of a deployment; `ModuleTestBuilder.WithTrust` sets one per test host). `RegisterMachine(machineId, publicKey, account, pairingSecretHash, selfSignature)`: the first registrant binds the account and stores the hash; later machines are accepted only with the same hash; a different key for the same machine id is still refused. Facts `ASecondMachineWithTheSamePairingSecretJoinsTheAccount` and `ASecondMachineWithoutThePairingSecretIsRefused` replace `ASecondMachineClaimingABoundAccountIsRefused`.
- **R53 — registration site (replaces the `IdentityStartup`/sign-in site of R47/R50).** Registration is a machine-side Platform startup task `MachineRegistration`, registered by `AddMachinePartLog`, running at silo start; the account is the Open-posture default owner (`DefaultLogin`) or the Secured bootstrap `Username`. It does not depend on sign-in; a machine serves one account in Stage B.
- **R54 — marketplace ownership (replaces R51's checks).** `MarketplaceRecord` carries `SourceAccount` and `SourceBrain` from `AppSource.Brain(AccountId, BrainId)` (both null for `Shipped`). The trust side accepts a signed Upsert when the payload account == `SourceAccount` and `IBrainAuthority(BrainScope.Create(SourceAccount, SourceBrain).Id).Account()` == the payload account; `Publisher` (a principal id) is recorded, not compared to the account. Remove checks the stored listing the same way.
- **Replay.** `MachineSigned<T>` carries a timestamp and a per-machine monotonic counter; the trust side rejects a timestamp outside ±5 minutes or a counter not greater than the last one seen for that machine.
- **R55 — one binding per account (refines R41/R52/R53).** A machine holds one binding per account it serves. `RegisterMachine` runs for the default owner at silo start and again, machine-side, whenever an account's brain is created on that machine (the R57 path); the pairing-secret rule (R52) applies per account; a signed payload is honoured when its account is among the machine's bindings.
- **R56 — shipped listings stay machine-side (replaces R45's first-party listings).** The trust catalog holds only published `AppSource.Brain` listings; `os/*` shipped listings keep today's merge in `MarketplaceView` from `FeatureCatalog`. `FeatureListingRegistration`, the trust host's features copy, Step 9's features key and the fact `FirstPartyListingsComeFromTheTrustHostsFeaturesCopy` are struck. No Roslyn/`AppDeclaration` in Platform.Trust.
- **R57 — the identity split.** The trust-side authority records membership and scope only and reports whether the brain is new; machine-side identity code calls `IBrain.Create` locally for a new brain. No trust grain calls a neuron.
- **R58 — a fifth client, `IIdentityClient` (Framework).** It carries the Account/Principal/BrainAuthority operations the machine-side identity code (IdentityStartup, IdentityEndpoints, grant filters) and `OwnerOf` need; Platform implements it over the gateway; Step 5's type-level fact covers it; Step 1's reflection fact includes the identity code.
- **R59 — boot provisioning.** At silo start, ahead of the Active-stage startup tasks, `MachineRegistration` puts the Open owner (or the Secured bootstrap user with its account and default brain) on record through `IIdentityClient`, then binds the machine; a fresh store boots without a lazy provision.
- **R60 — compute stays machine-side (amends R38).** `LedgerGrain`, `UsageGrain`, `TraceBudgetGrain` with their interfaces and state stay in Platform on the machine's `platform` storage for Stage B; there is no sixth client. `AMachineReachesTrustGrainsOnlyThroughTheClients` lists every trust grain that remains and fails on any other machine-side caller.
- **R61 — machine storage (amends Steps 6/9).** Machines keep a local `platform` storage (Secrets, Integration, ConnectionIndex, compute, principal credentials), but every machine has its own grain-state container (`grainstate-{name}`), never shared with another machine.
- **R62 — credentials stay machine-side.** Principal credentials (the password hash) live in a machine-side Platform grain; the trust cluster holds accounts, memberships and brain authority only. The boundary fact allows `Account`, `Member`, `Grant` by ruling and nothing credential-shaped.
- **R63 — signed identity writes.** Every `IIdentityClient` write that acts for an account (ProvisionOwner, EnsureMember, CreateBrain, EnsureBrain, GrantAsOwner, RevokeAsOwner, RegisterPrincipal) travels as `MachineSigned<T>` and is honoured only from a machine bound to that account; ProvisionOwner establishes the registering machine's binding in the same signed call (first registrant wins, R52). Membership reads stay unsigned and carry no credential.
- **R64 — release-part credential.** The AppHost's `release-part` command sends the Secured bootstrap Basic credential from the AppHost configuration; Open posture sends none.
- **R65 — persisted counter.** The per-machine signed-call counter is persisted in a `counter` file next to the identity key.
- **R66 — loopback trust silo in the AppHost.** In the dev AppHost the trust silo advertises and listens on 127.0.0.1 (silo and gateway ports), and machines use `localhost:30100`; a non-dev deployment sets the advertised address explicitly (Task 21).
- **R67 — a username belongs to its registering machine.** RegisterPrincipal and CompleteRegistration are honoured only from a paired machine; the trust principal records the registering machine, which alone completes it or signs the principal in; a refused registration removes the local credential.
- **R68 — Authorize is a read.** The unsigned decision never writes; a Once grant is spent by a signed Consume bound to the account; the Open owner's brains are provisioned through the signed ProvisionOwner (boot, or the machine's grant policy).
- **R69 — out of scope:** unsigned keyring TryAdd, node-directory writes and shard Put/Drop wait for Task 21.
- **R70 — client hosts compose the trust clients.** `AddTrustClients` serves a machine silo (signing) and an Orleans-client host such as the MCP (reading), through the private gateway client when `Trust:Gateway` is set.
- **R71 — allocated trust ports.** The AppHost's trust silo and gateway ports are Aspire endpoints (fixed only by `DigitalBrain:Platform:Trust:GatewayPort`/`SiloPort`), and machines dial the allocated gateway.

**Contract (fixed, plus R31's additive fourth client):** `src/DigitalBrain.Trust` (trust cluster host: sequencer, node directory, keyrings, ledger, identity, relay endpoint; `platform` storage only; the architecture test forbids any `Neuron` type in its composition) and the OS host becoming a machine host (`AddMachinePartLog`, `Role` from configuration). Machines reach trust grains only through `ISequencerClient`, `INodeDirectoryClient`, `IKeyringClient` — and `IMarketplaceClient` (R31) — (Stage A: direct grain calls; Task 21: the transport). Test `AMachineRunsAPartFromAnotherMachinesLog` (two machine hosts in one test process with separate directories against one trust host; A writes and releases, B acquires and reads). Acceptance: two machine processes on one PC hand a part back and forth, driven through `digitalbrain-mcp`.

**Files:**
- Create project `src/DigitalBrain.Platform/DigitalBrain.Platform.Trust/DigitalBrain.Platform.Trust.csproj` (references `DigitalBrain.Framework` only) and **move** into it exactly (R38): `Sequencer/SequencerGrain.cs` (`SequencerGrain`, `ISequencerGrain`), `Sequencer/HeadRecord.cs`; `Keys/KeyringGrain.cs` (`KeyringGrain`, `IKeyringGrain`); `Mesh/StorageNodeDirectoryGrain.cs`, `Mesh/SeedShardGrain.cs`, `Mesh/SeedNodeRegistration.cs` with their interfaces; `Identity/Authority/BrainAuthorityGrain.cs`, `AccountGrain.cs`, `PrincipalGrain.cs` with their interfaces and state; `Compute/LedgerGrain.cs`, `UsageGrain.cs`, `TraceBudgetGrain.cs` with their interfaces and state; and create `Marketplace/MarketplaceGrain.cs`, `Marketplace/FeatureListingRegistration.cs` (R45), `TrustHosting.cs`. **Everything else stays in Platform, machine-side:** `SeenVersions`, `MachineIdentity`, `Parts/*`, `MeshStore`, Secrets/Integration/ConnectionIndex grains and endpoints, `IdentityStartup`, `IntegrationSeeder`, `SecretsEndpoints`, `IdentityEndpoints`. A helper referenced from both sides moves to Platform.Trust when trust-only, to Framework when it is a contract type; the implementer confirms each moved file's references with LSP (`findReferences`) before the move and lists them in the task report.
- Create in `src/DigitalBrain.Platform/DigitalBrain.Platform/Trust/`: `ISequencerClient.cs`, `INodeDirectoryClient.cs`, `IKeyringClient.cs`, `GrainTrustClients.cs` (also implements `IMarketplaceClient`), `TrustOptions.cs`, `TrustGatewayClient.cs`, `MachineSigned.cs` (R37)
- Create: `src/DigitalBrain.Framework/DigitalBrain.Framework/Platform/Marketplace/IMarketplaceClient.cs`, `MarketplaceRecord.cs` (R37)
- Create: `src/DigitalBrain.Trust/DigitalBrain.Trust/DigitalBrain.Trust.csproj` (references `DigitalBrain.Platform.Trust` and `DigitalBrain.Framework` only), `Program.cs`; `src/DigitalBrain.Trust/DigitalBrain.Trust.Tests.Unit/TrustHostFacts.cs`
- Create: `src/Testing/DigitalBrain.Testing.Module/TrustHost.cs`
- Modify: `src/DigitalBrain.Platform/DigitalBrain.Platform/DigitalBrain.Platform.csproj` (reference Platform.Trust), `PlatformHosting.cs`, `Parts/MachineHosting.cs`, `Parts/PartLease.cs`, `Parts/PartLogs.cs`, `Keys/PartKeys.cs`, `Mesh/MeshStore.cs`
- Modify: `src/DigitalBrain.Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps/Marketplace/MarketplaceNeuron.cs` (stateless, delegates to `IMarketplaceClient`, `OwnerKey` removed), `src/DigitalBrain.Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps/Marketplace/MarketplaceWriteGuard.cs` (R44), `src/DigitalBrain.OS/DigitalBrain.OS/OsMachine.cs`, `src/DigitalBrain.AppHost/AppHost.cs`, `src/Testing/DigitalBrain.Testing.Module/ModuleTest.cs`, `ModuleTestBuilder.cs` (`WithTrust(TrustHost)`), `src/Testing/DigitalBrain.Architecture.Tests/StorageRingFacts.cs`, `ConfigurationKeyOwners.json`, `DigitalBrain.slnx` (and the local git-ignored `DigitalBrain-lsp.sln`)
- Test: `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Trust/TrustClientFacts.cs`, `Trust/TrustBoundaryFacts.cs`, `Trust/MarketplaceTrustFacts.cs`, `Parts/MachineHandoverFacts.cs`, `Parts/MachinePartEndpointFacts.cs`; `src/Testing/DigitalBrain.Architecture.Tests/TrustCompositionFacts.cs`

**Interfaces:**

```csharp
namespace DigitalBrain.Platform.Trust;   // internal; only wrapped keys, signed heads, sealed shards, machine public keys, and signed payloads cross them

internal interface ISequencerClient
{
    Task<HeadRecord?> Get(string partHash);
    Task<bool> CompareAndSet(string partHash, long expectedVersion, HeadRecord next);
}

internal interface INodeDirectoryClient
{
    Task RegisterMachine(string machineId, byte[] publicKey, string account, byte[] pairingSecretHash, byte[] selfSignature);   // R41/R52
    Task Register(string nodeId, string ownerAccount);
    Task SetOnline(string nodeId, bool online);
    Task ReportAudit(string nodeId, bool passed);
    Task<string[]> Place(int count, string[] exclude);
    Task<StorageNodeView[]> List();
    Task Put(string nodeId, string cid, byte[] sealedShard);
    Task<byte[]?> Get(string nodeId, string cid);
    Task<byte[]?> Prove(string nodeId, string cid, byte[] nonce);
    Task Discard(string nodeId, string cid);
}

internal interface IKeyringClient
{
    Task<byte[]?> Get(string brainId, string partId);            // wrapped part key
    Task<bool> TryAdd(string brainId, string partId, byte[] wrapped);
    Task<string?> OwnerOf(string brainId);                       // IBrainAuthority.Account() on the trust side (R36)
}

```

```csharp
namespace DigitalBrain.Framework.Platform.Marketplace;   // public, platform-facing (R37); mirrors IMarketplace's four members

// MarketplaceRecord: MarketplaceListing's fields (Publisher, Source, Name, Title, Description, Version, Icon) plus `string? SourceAccount` and `string? SourceBrain` (R54: from AppSource.Brain(AccountId, BrainId), both null for Shipped)

public interface IMarketplaceClient
{
    Task<MarketplaceRecord[]> List();
    Task<MarketplaceRecord?> Read(string publisher, string name);
    Task Upsert(CallerContext caller, MarketplaceRecord listing);   // sent as MachineSigned<…> with caller.AccountId
    Task Remove(CallerContext caller, string publisher, string name);
}
```

  `MarketplaceRecord` mirrors `MarketplaceListing`'s fields (LSP `documentSymbol` on `MarketplaceListing.cs`); `MarketplaceNeuron` maps between them and sets `MarketplaceRecord.SourceAccount`/`SourceBrain` (R54) from `AppSource.Brain`, null for `Shipped`. **Upsert/Remove ownership (R45):** `MarketplaceGrain` accepts a signed write only when the `MachineSigned` signature verifies, the payload account equals the machine's registered account (R41), and the listing's `Source` is `AppSource.Brain`, and the payload account equals `SourceAccount` **and** `IBrainAuthority.Account()` evaluated on `BrainScope.Create(SourceAccount, SourceBrain).Id` (R54; `Publisher` is recorded, not compared; Remove checks the stored listing the same way), and the `MachineSigned` timestamp is within ±5 minutes and its counter exceeds the machine's last one (replay); otherwise `UnauthorizedAccessException`. `Shipped` listings come only from `FeatureListingRegistration` under `platform`. **First-party listings (R45):** a Platform.Trust startup task `FeatureListingRegistration` upserts the features-owner listings from the features copy the trust host carries (`DigitalBrain:Modules:apps:Options:Features` on the trust host), reading each `app.cs` by syntax only (`AppDeclaration`, Roslyn; no compile) under account `platform`; signed machine writes for those publishers are refused. That startup task reads the key by raw `IConfiguration` (the trust host composes no modules); the key stays owned by the Apps module's options in `ConfigurationKeyOwners.json` (no new entry).

- The shard members mirror `IStorageShard` (LSP `documentSymbol` on `SeedShardGrain.cs` first).
- `GrainTrustClients(IGrainFactory grains)` implements all four over the trust grains. Without `DigitalBrain:Platform:Trust:Gateway` (every Stage A host, `ModuleTest`, OS unit/E2E) it is built on the silo's own `IGrainFactory` and `AddDigitalBrainPlatform` also calls `AddTrustCluster`, so those hosts compose as today. With a gateway it is built on `TrustGatewayClient.Grains` (R34).
- `TrustOptions` (key `DigitalBrain:Platform:Trust`): `string? Gateway` (`host:port`), `string ClusterId = "digitalbrain-trust"`, `string ServiceId = "digitalbrain-trust"`; `Validate()`; added to `ConfigurationKeyOwners.json`.
- `TrustHosting.AddTrustCluster(ISiloBuilder)` (in Platform.Trust, R38): the moved grains, `SeedNodeRegistration` with the seed count option, the `MachineSigned` verifier, and a relay endpoint placeholder that Task 21 fills. It does **not** register `IdentityStartup`, `IntegrationSeeder`, the Secrets/Identity/Integration endpoints, `AddMasterKeyWrapper` or `IMasterKeyCustody` (R30), `AddDigitalBrain`, modules, `cluster` storage, `IPartKeys` or `PartsOptions`. Secrets/Integration/ConnectionIndex grains stay machine-side in Platform (R38); their state never reaches trust storage.
- `MachineHosting.AddMachinePartLog`: `PartsOptions`, `MachineIdentity` (R53: registration is the machine-side startup task `MachineRegistration` at silo start, account = Open-posture `DefaultLogin` or Secured bootstrap `Username`, independent of sign-in; `selfSignature` covers machineId‖publicKey‖account‖pairingSecretHash; no token is sent; R52 pairing secret from `DigitalBrain:Platform:Parts:PairingSecret`), `SeenVersions`, `AddMasterKeyWrapper`, `IMasterKeyCustody`, `IPartKeys`, `IMeshStore`, `PartLogs`, part-log storage and journal, the four clients, and the machine-host endpoints `POST /machine/parts/{part}/release` and `GET /machine/parts/{part}/records/{key}` (R40: both require the authenticated principal's account == `IKeyringClient.OwnerOf(brain of part)`, else 403; the records endpoint returns the stored record as the grain serializer's JSON, and **409** when this machine does not hold the part — it never acquires implicitly).
- `PartLogs.Release(PartId)`: deactivate that part's activations, flush, `PartLease.Release`; `PartLogs.Opened` lists the parts this silo holds.

- [ ] **Step 1: Failing facts — clients.** `TrustClientFacts`: `AMachineReachesTrustGrainsOnlyThroughTheClients` (reflection over the types that call trust grains: `PartLease`, `PartKeys`, `MeshStore`, `PartLogs` have no `IGrainFactory`/`IClusterClient` constructor parameter — `SecretsStore` and `ModuleIntegrations` are not listed because they call machine-local grains; `DigitalBrain.Apps.MarketplaceNeuron` takes `IMarketplaceClient` and has no `[PersistentState]` parameter; `MarketplaceWriteGuard` takes `IMarketplaceClient`), `TheGrainClientsRoundTripAHeadAWrappedKeyAShardAndAListing` (a `ModuleTest` host), `AMachineRegistersItsKeyOnceAndADifferentKeyIsRefused` (R41: same key twice succeeds; a second key for the same id throws), `ASecondMachineWithTheSamePairingSecretJoinsTheAccount` and `ASecondMachineWithoutThePairingSecretIsRefused` (R52), `ASignedPayloadForAnotherAccountIsRefused` (R41), `AReplayedSignedPayloadIsRefused` (replay: stale timestamp or non-increasing counter). `MarketplaceTrustFacts`: `AMarketplaceWriteWhoseAccountIsNotThePublishersBrainOwnerIsRefused` and `FirstPartyListingsComeFromTheTrustHostsFeaturesCopy` (R45: a trust host started on a temp features folder with one `features/os/x/app.cs` lists `os/x` under account `platform` with no compile). Run `dotnet test --project src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit -- --filter-class "*TrustClientFacts*"` and `... "*MarketplaceTrustFacts*"` → FAIL.
- [ ] **Step 2: Implement** the four interfaces, `GrainTrustClients`, the constructor swaps, `MachineSigned`, `RegisterMachine`, `MarketplaceGrain`, `FeatureListingRegistration`, the stateless `MarketplaceNeuron` and `MarketplaceWriteGuard` over `IMarketplaceClient`, remove the `TrustClusterNeurons` entry. LSP diagnostics clean; Platform, Apps and Architecture (6 known) suites → PASS. Commit: `Trust: machines reach the sequencer, node directory, keyrings and marketplace only through four clients`.
- [ ] **Step 3: Failing fact — trust composition (R33).** `TrustCompositionFacts.TheTrustHostComposesNoNeuron`: (a) for every assembly in `typeof(DigitalBrain.Trust.Program).Assembly`'s transitive references except `DigitalBrain.Framework` (and BCL/Orleans/Microsoft), no type derives from `Neuron`; (b) building `AddTrustCluster()` on a bare silo builder registers no keyed `IGrainStorage` named `cluster`. Run `dotnet test --project src/Testing/DigitalBrain.Architecture.Tests -- --filter-class "*TrustCompositionFacts*"` → FAIL (the project does not exist).
- [ ] **Step 4: Implement** `DigitalBrain.Platform.Trust` (the moves listed), `TrustHosting`, `DigitalBrain.Trust` project and `Program.cs` (generic host; Orleans with Azure blob `platform` storage through the Orleans module's offered integration from 8c5677e48 — LSP `goToDefinition` on it — and `AddTrustCluster()`). Build each project; Platform, Architecture, Apps suites → PASS. Commit: `Trust: trust grains live in their own project and the trust host composes no neuron`.
- [ ] **Step 5: Failing facts — the trust boundary (R46).** `TrustBoundaryFacts.TheTrustHostRegistersNoSecretOrCustodyService` (reflection over a silo built with only `AddTrustCluster()`: no registration of `ISecrets`, `IKeyWrapper`, `IMasterKeyCustody` or `IModuleIntegrations<>`) and `NoTrustClientCarriesPlaintextOrKeys` (type-level over the four client interfaces: every parameter and return type, recursively through record members, is a primitive/string identifier, `HeadRecord`, `StorageNodeView`, `MarketplaceRecord`, `MachineSigned<T>`, or `byte[]` named `wrapped`/`sealedShard`/`publicKey`/`selfSignature`/`nonce` — never a key type, `SecretRef`, `IntegrationState` or a plaintext string field; extend the allowlist only with a controller ruling). Run → RED before Step 4's composition exists, then PASS. Commit: `Trust: the trust host holds no secret, credential or custody, and no client carries plaintext or keys`.
- [ ] **Step 6: Gateway wiring (R34).** `TrustGatewayClient` is a hosted service in the machine process that builds a **private** `Host` with `UseOrleansClient(client => client.UseStaticClustering(gateway).Configure<ClusterOptions>(trust ids))`, starts it before the silo's startup tasks (registered with `ServiceLifecycleStage.RuntimeInitialize` — LSP-check `SeedNodeRegistration`'s stage), and exposes only `GrainTrustClients` over that client's `IClusterClient`; that client is never registered as the silo's `IGrainFactory`/`IClusterClient`. `TrustHost` (Testing.Module): a one-silo `InProcessTestCluster` with `AddTrustCluster()`, memory `platform` storage, 8 seed nodes, default owner provisioned; its `GatewayPort` is read off the started cluster's silo (R40: the `InProcessTestCluster` assigns it; the test never sets it in `ConfigureSilo`); `ModuleTestBuilder.WithTrust(TrustHost trust)` sets `DigitalBrain:Platform:Trust:Gateway = 127.0.0.1:{trust.GatewayPort}` plus distinct `ClusterId`/`ServiceId` per machine and drops the machine's `platform` storage. Two machine silos are two `ModuleTest` clusters in the same process, each with its own private trust client. Fix readiness, never a longer timeout; kill runs over 5 minutes. Commit: `Trust: a machine reaches a separate trust cluster through a private gateway client`.
- [ ] **Step 7: Failing fact — the handover (R32).** `MachineHandoverFacts.AMachineRunsAPartFromAnotherMachinesLog` is a **part-log-level** fact: trust host + machines A and B as in Step 6 (separate `MachineDirectory`s, `Role = Own`). A creates the brain; `part = PartId.Of("{scope}/apps/counter/c1")`; `var log = await aLogs.For(part)`; append a record for key `{scope}/apps/counter/c1` (state name `counter`, value 3); `await aLogs.Release(part)`; `var read = (await bLogs.For(part)).Read(...)` → the record with value 3, served from A's segments in the mesh and the wrapped key from the keyring; B appends 4 and releases; A's `For(part)` reads 4. Assert B's `PartLogs.Opened` never contains the core part. A neuron *activation* on B is out of scope: it would need the core part's kernel records (instance directory, type table), held only by the owner's machine — that is Task 22 (cross-part calls). Run → FAIL, implement `PartLogs.Release`/`Opened`, → PASS. `MachinePartEndpointFacts`: the release endpoint releases and the records endpoint returns the stored record (owner caller), 403 for non-owners. Commit: `Parts: a machine runs a part from another machine's log`.
- [ ] **Step 8: Carried from Stage A.** `ModuleTest` provisions the default brain on every host (R16); run every module unit suite once with `WithPartLog()` forced on in a scratch run (not committed) and record the remaining failures in the ledger. R25: record in the ledger that it stays parked (default). Commit: `Testing: every module host starts with its default brain provisioned`.
- [ ] **Step 9: AppHost.** A `trust` resource (the `DigitalBrain.Trust` project, Azurite `platform` storage, gateway port 30100, and `DigitalBrain:Modules:apps:Options:Features` set to the same repo `features/` folder the OS machine gets, so `FeatureListingRegistration` (R45) reads `app.cs` by syntax; the Platform.Trust startup task reads it by raw configuration, and the published trust image carries a `{app}/features` copy like the OS image; `ConfigurationKeyOwners.json` needs no entry — the owner stays Apps). The OS role gets `Trust:Gateway` and no `platform` storage. A second machine resource `OperatingSystem-b`: own `MachineDirectory`, `Role = Own`, own cluster id, Orleans silo/gateway ports **11112/30001** (A keeps the defaults, R40), HTTP port **5095** and MCP at `http://localhost:5095/mcp` (A keeps the run's chosen ports, e.g. 5090/5091 while the peer session holds 5080/5081). Each machine resource gets an Aspire command `release-part` (argument: part id) that calls `POST /machine/parts/{part}/release` on that machine. Build each project; a copy-lock on the AppHost/OS bin is not a compile error. `TrustHostFacts.TheTrustHostStartsWithNoModules` (`DigitalBrain.Aspire.Testing`, Azurite; skips without a container runtime). Commit: `Trust: the AppHost runs the trust cluster beside two machine hosts`.
- [ ] **Step 10: Acceptance (live).** `dotnet run digitalbrain.cs` on free ports; `mcp__aspire__list_resources` all Healthy (`trust`, `OperatingSystem-a`, `OperatingSystem-b`). Through A's `digitalbrain-mcp`: `neurons_context`; `neurons_call` a mutation on an app-part neuron; `release-part` on A (`mcp__aspire__execute_resource_command`); `GET http://localhost:5095/machine/parts/{part}/records/{key}` on B → A's state; `release-part` on B; mutate again on A through MCP (A reacquires). `list_traces`: `ISequencerGrain/CompareAndSet` on the same part hash from both machines, served by `trust`; `list_structured_logs`: no `LeaseLost`, `RollbackDetected`, `JournalConflict`. Record in the ledger.
- [ ] **Step 11: Regression net.** Platform, Architecture (6 known), Apps, OS unit, Trust suites. Commit only the files above, by explicit path.

### Task 19: Custody in hardware (Windows module)

**Contract (fixed):** `TpmMasterKeyCustody : IMasterKeyCustody` and a TPM-backed machine key behind the Windows module's per-OS contract; `DigitalBrain:Platform:Parts:Custody = Tpm|Configured`, with `Configured` refused in production. TPM facts skip when no TPM is present (the repo's live-gated pattern); a fact that production refuses configured custody.

**Files:**
- Move: `src/DigitalBrain.Platform/DigitalBrain.Platform/Keys/IMasterKeyCustody.cs` → `src/DigitalBrain.Framework/DigitalBrain.Framework/Parts/IMasterKeyCustody.cs` (public; H6)
- Create: `src/DigitalBrain.Framework/DigitalBrain.Framework/Parts/IMachineSigningKey.cs` (R35); `src/DigitalBrain.Platform/DigitalBrain.Platform/Sequencer/FileMachineSigningKey.cs` (today's `identity.key` behaviour)
- Create: `src/DigitalBrain.Modules/Microsoft/Windows/DigitalBrain.Modules.Microsoft.Windows/TpmMasterKeyCustody.cs`, `TpmMachineSigningKey.cs`
- Modify: `src/DigitalBrain.Modules/Microsoft/Windows/DigitalBrain.Modules.Microsoft.Windows/WindowsModule.cs`; `src/DigitalBrain.Platform/DigitalBrain.Platform/Parts/PartsOptions.cs` (`Custody`), `Parts/MachineHosting.cs`, `PlatformHosting.cs`, `Sequencer/MachineIdentity.cs`, `Keys/ConfiguredMasterKeyCustody.cs`, `Keys/PartKeys.cs`
- Test: `src/DigitalBrain.Modules/Microsoft/Windows/tests/DigitalBrain.Modules.Microsoft.Windows.Tests/TpmCustodyFacts.cs`, `src/DigitalBrain.Platform/DigitalBrain.Platform.Tests.Unit/Parts/CustodyOptionsFacts.cs`

**Interfaces:**
- `public enum MasterKeyCustodyKind { Configured = 0, Tpm = 1 }`; `PartsOptions.Custody` (key `DigitalBrain:Platform:Parts:Custody`, default `Configured`; `PartsOptions.cs` is already in `ConfigurationKeyOwners.json`).
- `public interface IMasterKeyCustody { byte[] Wrap(string accountId, string context, byte[] secret); byte[] Unwrap(string accountId, string context, byte[] wrapped); }` — Framework, signature unchanged.
- `public interface IMachineSigningKey { byte[] Sign(byte[] data); byte[] PublicKey { get; } }` — Framework (R35); `MachineIdentity` consumes it instead of loading `identity.key` itself; `FileMachineSigningKey` is the default registration.
- `TpmMasterKeyCustody` (R35): per account, a persisted RSA-2048 key in the Microsoft Platform Crypto Provider (`CngKey.Create(CngAlgorithm.Rsa, "digitalbrain-{account}", new CngKeyCreationParameters { Provider = new CngProvider("Microsoft Platform Crypto Provider"), ExportPolicy = CngExportPolicies.None })`). `Wrap`: random 32-byte DEK → AES-GCM over the secret (associated data = `context`) → DEK wrapped with RSA-OAEP-SHA256 under the TPM key; output = wrapped DEK ‖ nonce ‖ tag ‖ ciphertext. `Unwrap` reverses it. `static bool IsAvailable()` opens the provider. Verify CNG API details with microsoft-learn before coding.
- `TpmMachineSigningKey : IMachineSigningKey`: a persisted ECDsa P-256 key (`CngAlgorithm.ECDsaP256`) in the same provider, non-exportable; `PublicKey` = SubjectPublicKeyInfo.
- `WindowsModule` registers both when `Custody = Tpm`. `MachineHosting` validates at start: `Custody = Configured` while `ASPNETCORE_ENVIRONMENT` (the host's `IHostEnvironment.EnvironmentName`) is `Production` → start refused with `InvalidOperationException` naming `DigitalBrain:Platform:Parts:Custody`; `Custody = Tpm` with no TPM custody registered or `!IsAvailable()` → refused the same way.

- [ ] **Step 1: Move `IMasterKeyCustody` and add `IMachineSigningKey`** (LSP `findReferences` on `IMasterKeyCustody` and on `MachineIdentity`'s key use); `FileMachineSigningKey` keeps today's file behaviour. Build Framework and Platform; Platform suite → PASS. Commit: `Keys: master key custody and the machine signing key are framework contracts a per-OS module can implement`.
- [ ] **Step 2: Failing facts.** `CustodyOptionsFacts.ProductionRefusesConfiguredCustody` (a `WithPartLog()` host with environment `Production`, `Custody = Configured` → start throws naming the key) and `TpmCustodyWithoutAProviderIsRefused`. `TpmCustodyFacts`, each `Assert.Skip` when `!TpmMasterKeyCustody.IsAvailable()`: `AWrappedSecretUnwrapsThroughTheSameTpmKey`, `TheTpmKeyIsNotExportable`, `TheMachineSignsHeadsWithItsTpmKey` (a `HeadRecord` signed through `TpmMachineSigningKey` verifies with its `PublicKey`). Run both suites → FAIL (SKIP on a TPM-less runner, reported as such).
- [ ] **Step 3: Implement** `Custody`, the start validation, `TpmMasterKeyCustody`, `TpmMachineSigningKey`, the Windows module registration. Platform and Windows suites → PASS/SKIP. Commit: `Windows: master key custody and the machine signing key live in the TPM; production refuses configured custody`.
- [ ] **Step 4: Live.** `Get-Tpm` on this PC, recorded; the AppHost sets `Custody = Tpm` on the machine resources; restart one; `digitalbrain-mcp` `neurons_get` reads a neuron's state back (its part key unwrapped through the TPM); `list_structured_logs` shows no custody errors. Record in the ledger.


## Plan 6 — Transport (Tasks 20–22)

### Task 20 (gate): NAT traversal spike
- Measure direct QUIC (`System.Net.Quic`) hole punching between two home networks and the relay fallback; record success rates and latency. Write tasks for 21–22.

### Task 21: Machine links and the relay
- **Produces:** `IMachineTransport { Task<IMachineLink> Connect(string machineId, CancellationToken); }`, mutually authenticated with machine identities, relay carrying ciphertext only; the trust clients (`ISequencerClient`, `INodeDirectoryClient`, `IKeyringClient`) and the mesh's shard calls over it.
- **Tests:** loopback round-trip; a relay that flips a byte is detected; an unregistered machine is refused.

### Task 22: Cross-part calls
- **Produces:** a grain call filter that sends a call for a part not leased here to the head record's holder over its link, with the `Trace`; no holder → 503 (Q15).
- **Tests:** part A on host 1 calls a neuron of part B on host 2; B unleased → 503; one trace id spans both.
- **Release gate:** Stage A acceptance repeated with the trust cluster, one own machine and one cloud machine (`Role = Cloud`) as separate processes; the cloud machine is refused the core and private parts; with the trust cluster stopped, the own machine keeps accepting writes (journal) and commits them when it returns (Q26).

# Stage C — network

## Plan 7 — Volunteer storage + credits (Tasks 23–25)

### Task 23 (gate): node protocol
- Write step-level tasks for 24–25: put/get/prove/drop message shapes, quotas, signed registration.

### Task 24: Volunteer storage node
- **Produces:** a storage service on any machine answering put/get/prove/drop over its link; `MeshStore` addresses shards through `IShardClient` (seed grain or remote link); registration in the directory signed by the machine identity, owner = the machine's account.
- **Tests:** `ALyingNodeServesOtherBytesAndTheReadSurvives` (the tamper fact plan 1 deferred); a node that answers proofs without data fails audit; an offline node's shards are repaired onto other owners; placement now spreads across distinct owners when they exist (Q25).

### Task 25: Credits for storage
- **Produces:** audit passes credited to the node owner through the ledger (`ILedgerGrain.Credit`, entry id `audit/{node}/{segment}/{round}`, so a replayed audit credits once), credits only (Q21).
- **Tests:** an audit pass credits once; a failed audit credits nothing.

## Plan 8 — Circles (Tasks 26–28)

### Task 26 (gate): circle authorization
- Spec open item 5 is an open design front: propose and wait for the user, no code.

### Task 27: Circle neuron and key sharing
- **Produces:** `ICircle` neuron (members, contributed machines, schedules); sharing a part wraps its key to each member machine with ECDH P-256 + HKDF (context `circle|{circle}|{part}`) in the keyring; revoking rotates the part key and re-snapshots under it.
- **Tests:** a member machine unwraps the key; a removed member cannot read anything written after the rotation.

### Task 28: Shared connections and lease preference
- **Produces:** sharing asks which bound connections go along (Q14); lease preference own → circle, returning to an own machine at the next quiet moment (Q17); the core never leaves own machines (Q11).
- **Tests:** a circle machine is refused the core; with the owner back online the lease returns after the part goes quiet.

## Plan 9 — Cloud and public compute (Tasks 29–30)

### Task 29 (gate): Wasmtime determinism
- Run one `wasm32-wasip2` job on two machines with fuel metering; confirm identical output and fuel; write tasks for 30.

### Task 30: Workers, verification, fuel
- **Produces:** workers on cloud and volunteer machines running deterministic jobs only for parts marked not private (Q2, Q7, Q24); ComputeMesh-style redundant verification; payment by fuel through the ledger (credits only).
- **Tests:** two agreeing workers are credited; disagreement re-runs on a third and the dissenter's reputation drops; a private part's job is refused.

---

## Spec coverage

| Spec | Task |
|---|---|
| Q1, Q8 | 12–13 (state is the log; the in-memory map and the journal are the machine's cache) |
| Q2, Q4, Q7 | 11 (core only on own machines), 22 release gate, 30 |
| Q3, Q6 | 4–6, 19, 27 |
| Q5, Q11, Q17 | 11, 28 |
| Q9, Q27 | 2, 10, 13, 14 |
| Q10 | 7–8, 11 |
| Q12, Q24 | 1, 17–18 |
| Q13 | 14, 16 |
| Q14 | 28 |
| Q15 | 11, 21–22 |
| Q16, Q19, Q20, Q25 | 3, Stage A acceptance, 24 |
| Q18, Q26 | 12 |
| Q21 | 25 |
| Q22, Q23 | Platform/Mesh (shipped), 3, 15 |
