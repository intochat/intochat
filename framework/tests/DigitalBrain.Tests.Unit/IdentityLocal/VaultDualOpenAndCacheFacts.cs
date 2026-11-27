using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Identity.Local.Vault;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Identity.Local.Tests.Unit;

/// <summary>
/// Verifies OS-04a/b invariants in <c>DigitalBrain</c> and <c>DigitalBrain.Identity.Local</c>:
/// <list type="bullet">
/// <item><c>mk3:</c> (both raw AES-GCM and <see cref="SegmentSeal"/> AAD) to <c>mk4:</c> zero-wipe dual-open.</item>
/// <item><c>brain:</c> (both legacy <see cref="ILegacyBrainKeyReader"/> and <see cref="IMasterKeyCustody"/>) to <c>brain2:</c> zero-wipe dual-open without creating phantom keys.</item>
/// <item><see cref="CachingSecretValueSource"/> write-through updates, TTL expiry, app caller refusal, and fail-closed backend errors.</item>
/// <item><see cref="SecretCoordinate"/> deterministic collision-free coordinates and AAD formatting.</item>
/// <item><see cref="VaultNeuron"/> capability-policy refusal for ephemeral brains and <see cref="Actor.Module"/> vault ownership.</item>
/// </list>
/// </summary>
public sealed class VaultDualOpenAndCacheFacts
{
    private static readonly byte[] TestMasterBytes = Encoding.UTF8.GetBytes("super-secret-master-key-for-os04-tests");

    [Fact]
    public void MasterKeyWrapper_emits_mk4_and_dual_opens_both_legacy_mk3_formats()
    {
        var ownerKey = SecretCipher.NewKey();
        var custody = new ConfiguredMasterKeyCustody(TestMasterBytes);

        // 1. New write emits mk4:
        var wrapper = new MasterKeyWrapper(custody, TestMasterBytes);
        var mk4 = wrapper.Wrap(ownerKey);
        Assert.StartsWith(MasterKeyWrapper.CurrentPrefix, mk4, StringComparison.Ordinal);
        Assert.Equal(ownerKey, wrapper.Unwrap(mk4));

        // 2. Legacy mk3: produced by IMasterKeyCustody (SegmentSeal with AAD)
        var legacyCustodyMk3 = MasterKeyWrapper.LegacyPrefix
            + Convert.ToBase64String(custody.Wrap("local", "DigitalBrain.OwnerKeys.v3", ownerKey));
        Assert.Equal(ownerKey, wrapper.Unwrap(legacyCustodyMk3));

        // 3. Legacy mk3: produced by raw ConfiguredKeyWrapper (AES-GCM without AAD)
        var legacyV3Key = HKDF.DeriveKey(HashAlgorithmName.SHA256, TestMasterBytes, 32, info: MasterKeyWrapper.OwnerKeysContext.ToArray());
        var rawPayload = new byte[12 + 16 + ownerKey.Length];
        RandomNumberGenerator.Fill(rawPayload.AsSpan(0, 12));
        using (var aes = new AesGcm(legacyV3Key, 16))
        {
            aes.Encrypt(rawPayload.AsSpan(0, 12), ownerKey, rawPayload.AsSpan(28), rawPayload.AsSpan(12, 16));
        }

        var legacyRawMk3 = MasterKeyWrapper.LegacyPrefix + Convert.ToBase64String(rawPayload);
        Assert.Equal(ownerKey, wrapper.Unwrap(legacyRawMk3));

        // 4. Raw-key constructor also emits mk4: and dual-opens both mk3: layouts
        var rawWrapper = new MasterKeyWrapper(TestMasterBytes);
        var rawMk4 = rawWrapper.Wrap(ownerKey);
        Assert.StartsWith(MasterKeyWrapper.CurrentPrefix, rawMk4, StringComparison.Ordinal);
        Assert.Equal(ownerKey, rawWrapper.Unwrap(rawMk4));
        Assert.Equal(ownerKey, rawWrapper.Unwrap(legacyCustodyMk3));
        Assert.Equal(ownerKey, rawWrapper.Unwrap(legacyRawMk3));
    }

    [Fact]
    public async Task LocalSecretSealer_emits_brain2_and_dual_opens_legacy_brain_envelopes_without_creating_keys()
    {
        var custody = new ConfiguredMasterKeyCustody(TestMasterBytes);
        var legacyBrainKey = SecretCipher.NewKey();
        var reader = new StubLegacyBrainKeyReader(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["brain-legacy"] = legacyBrainKey,
        });
        var sealer = new LocalSecretSealer(custody, new MasterKeyWrapper(custody, TestMasterBytes), reader);

        // 1. New write emits brain2:
        var v2 = await sealer.Seal("brain-new", "token", "dummy-value-one-one-one");
        Assert.StartsWith("brain2:", v2.KeyId, StringComparison.Ordinal);
        Assert.Equal("dummy-value-one-one-one", await sealer.Open("brain-new", v2));
        Assert.Equal(0, reader.Calls);

        // 2. Legacy brain: produced by LocalSecretSealer (IMasterKeyCustody.Wrap)
        var dataKey1 = SecretCipher.NewKey();
        var legacyCustodySealed = new SealedSecret
        {
            KeyId = "brain:" + Convert.ToBase64String(custody.Wrap("brain-custody", "brain-secret", dataKey1)),
            Cipher = SecretCipher.Seal(dataKey1, "custody-secret-value"),
        };
        Assert.Equal("custody-secret-value", await sealer.Open("brain-custody", legacyCustodySealed));
        Assert.Equal(0, reader.Calls);

        // 3. Legacy brain: produced by OS BrainSecretSealer (BrainStorageKeys + SecretCipher)
        var dataKey2 = SecretCipher.NewKey();
        var legacyStorageSealed = new SealedSecret
        {
            KeyId = "brain:" + SecretCipher.Seal(legacyBrainKey, Convert.ToBase64String(dataKey2)),
            Cipher = SecretCipher.Seal(dataKey2, "legacy-storage-secret-value"),
        };
        Assert.Equal("legacy-storage-secret-value", await sealer.Open("brain-legacy", legacyStorageSealed));
        Assert.Equal(1, reader.Calls);

        // 4. Legacy brain:v1: prefixed payload also opens via read-only reader
        var legacyV1Sealed = new SealedSecret
        {
            KeyId = "brain:v1:" + SecretCipher.Seal(legacyBrainKey, Convert.ToBase64String(dataKey2)),
            Cipher = SecretCipher.Seal(dataKey2, "legacy-v1-secret-value"),
        };
        Assert.Equal("legacy-v1-secret-value", await sealer.Open("brain-legacy", legacyV1Sealed));
        Assert.Equal(2, reader.Calls);

        // 5. Missing legacy brain key fails closed without creating a phantom key
        await Assert.ThrowsAsync<CryptographicException>(() => sealer.Open("brain-missing", legacyStorageSealed));
        Assert.False(reader.CreatedAnyKey);
    }

    [Fact]
    public async Task CachingSecretValueSource_write_through_ttl_app_refusal_and_fail_closed()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 27, 1, 0, 0, TimeSpan.Zero));
        await using var os = TestOS.Local();
        var edge = os.Edge;
        var ambient = os.Callers;
        var loadCalls = 0;
        var backendDown = false;
        var backing = new List<string> { "short", "dummy-alpha-aaa", "dummy-alpha-aaa-longer" };

        var cache = new CachingSecretValueSource(
            (owner, _) =>
            {
                loadCalls++;
                if (backendDown)
                {
                    throw new IOException("External vault unreachable");
                }

                return ValueTask.FromResult<IReadOnlyList<string>>([.. backing]);
            },
            ambient,
            clock,
            TimeSpan.FromMinutes(5));

        var brain = new BrainId("brain-1");
        using (edge.Enter(edge.Person("alice", "acc-1", brain)))
        {
            // Cold load filters < 8 chars and sorts longest-first
            var values = await cache.CurrentValues(TestContext.Current.CancellationToken);
            Assert.Equal(["dummy-alpha-aaa-longer", "dummy-alpha-aaa"], values);
            Assert.Equal(1, loadCalls);

            // Warm cache hits without calling backend
            var cached = await cache.CurrentValues(TestContext.Current.CancellationToken);
            Assert.Equal(values, cached);
            Assert.Equal(1, loadCalls);

            // Write-through NotifySet updates warm entry immediately
            cache.NotifySet("alice", "dummy-beta-bbbbbbbbbbbbbb");
            var afterSet = await cache.CurrentValues(TestContext.Current.CancellationToken);
            Assert.Equal("dummy-beta-bbbbbbbbbbbbbb", afterSet[0]);
            Assert.Equal(1, loadCalls);

            // NotifyRemove invalidates owner entry -> reloads on next read
            cache.NotifyRemove("alice");
            _ = await cache.CurrentValues(TestContext.Current.CancellationToken);
            Assert.Equal(2, loadCalls);

            // TTL expiry triggers reload; if backend is down, fail closed (never return empty)
            clock.Advance(TimeSpan.FromMinutes(6));
            backendDown = true;
            await Assert.ThrowsAsync<IOException>(async () => await cache.CurrentValues(TestContext.Current.CancellationToken));
        }

        // App principal is unconditionally refused (zero-token auth enforcement)
        using (edge.Enter(edge.App("sandbox", new Actor.Person("alice", "acc-1"), brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await cache.CurrentValues(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void SecretCoordinate_is_deterministic_collision_free_and_keyvault_safe()
    {
        var c1 = SecretCoordinate.Format("prod-east", "alice", "openai:api-key", "person");
        var c2 = SecretCoordinate.Format("prod-east", "alice", "openai:api-key", "person");
        var c3 = SecretCoordinate.Format("prod-east", "bob", "openai:api-key", "person");
        var c4 = SecretCoordinate.Format("prod-west", "alice", "openai:api-key", "person");

        Assert.Equal(c1, c2);
        Assert.NotEqual(c1, c3);
        Assert.NotEqual(c1, c4);
        Assert.StartsWith("ic-", c1, StringComparison.Ordinal);
        Assert.StartsWith(SecretCoordinate.OwnerPrefix("prod-east", "alice", "person"), c1, StringComparison.Ordinal);
        Assert.All(c1, ch => Assert.True(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-'));

        var aad1 = SecretCoordinate.FormatAad("prod-east", "alice", "openai:api-key");
        var aad2 = SecretCoordinate.FormatAad("prod-east", "bob", "openai:api-key");
        Assert.NotEqual(aad1, aad2);

        // Length-prefixed AAD prevents delimiter ambiguity when '|' appears inside components.
        Assert.NotEqual(
            SecretCoordinate.FormatAad("a|b", "c", "d"),
            SecretCoordinate.FormatAad("a", "b|c", "d"));
    }

    [Fact]
    public async Task LocalVaultPort_enforces_CanStore_and_CanRemove_before_elevating_to_kernel()
    {
        var ct = TestContext.Current.CancellationToken;
        var custody = new ConfiguredMasterKeyCustody(TestMasterBytes);
        var services = new ServiceCollection();
        services.AddSingleton<IMasterKeyCustody>(custody);
        var provider = services.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = provider);
        os.Register<VaultNeuron>();

        var port = new LocalVaultPort(
            (_, fullKey) =>
            {
                var sep = fullKey.IndexOf('/', StringComparison.Ordinal);
                return os.Get<IVault>(new NeuronId(new BrainId(fullKey[..sep]), IVault.Type, fullKey[(sep + 1)..]));
            },
            custody,
            edge: os.Edge,
            callers: os.Callers);

        var brain = new BrainId("brain-1");
        var alice = new Actor.Person("alice", "acc-1");

        // Direct owner can store a secret.
        using (os.Edge.Enter(os.Edge.Person("alice", "acc-1", brain)))
        {
            var stored = await port.Set("item", "Item", "dummy-val-aaa", ct); // gitleaks:allow
            Assert.Equal(SecretVaultStatus.Ok, stored.Status);
        }

        // Actor.App and relayed Actor.Person (Direct: false) are denied on Set.
        using (os.Edge.Enter(os.Edge.App("sandbox", alice, brain)))
        {
            var deniedApp = await port.Set("item", "Item", "dummy-val-bbb", ct); // gitleaks:allow
            Assert.Equal(SecretVaultStatus.Denied, deniedApp.Status);
        }

        using (os.Edge.Enter(os.Edge.RelayedPerson("alice", "acc-1", brain)))
        {
            var deniedRelayed = await port.Set("item", "Item", "dummy-val-ccc", ct); // gitleaks:allow
            Assert.Equal(SecretVaultStatus.Denied, deniedRelayed.Status);
        }

        // Actor.App, Actor.Agent, and relayed Actor.Person (Direct: false) are denied on Remove.
        using (os.Edge.Enter(os.Edge.App("sandbox", alice, brain)))
        {
            Assert.Equal(SecretVaultStatus.Denied, await port.Remove("item", ct));
        }

        using (os.Edge.Enter(os.Edge.Agent(alice, brain)))
        {
            Assert.Equal(SecretVaultStatus.Denied, await port.Remove("item", ct));
        }

        using (os.Edge.Enter(os.Edge.RelayedPerson("alice", "acc-1", brain)))
        {
            Assert.Equal(SecretVaultStatus.Denied, await port.Remove("item", ct));
        }

        // Direct owner can still remove.
        using (os.Edge.Enter(os.Edge.Person("alice", "acc-1", brain)))
        {
            Assert.Equal(SecretVaultStatus.Ok, await port.Remove("item", ct));
        }
    }

    [Fact]
    public async Task VaultNeuron_blocks_ephemeral_brains_via_capability_policy_and_supports_module_owner()
    {
        var custody = new ConfiguredMasterKeyCustody(TestMasterBytes);
        var policy = new DenyEphemeralPolicy();
        var services = new ServiceCollection();
        services.AddSingleton<IMasterKeyCustody>(custody);
        services.AddSingleton<IBrainCapabilityPolicy>(policy);
        var provider = services.BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = provider);
        os.Register<VaultNeuron>();

        // LocalVaultPort enforces IBrainCapabilityPolicy before crossing into system/ vault
        var port = new LocalVaultPort(
            (_, fullKey) =>
            {
                var sep = fullKey.IndexOf('/', StringComparison.Ordinal);
                return os.Get<IVault>(new NeuronId(new BrainId(fullKey[..sep]), IVault.Type, fullKey[(sep + 1)..]));
            },
            custody,
            edge: os.Edge,
            callers: os.Callers,
            capabilityPolicy: policy);

        using (os.Edge.Enter(os.Edge.Person("try-guest1", "try-guest1", new BrainId("try-guest1"))))
        {
            var denied = await port.Set("item", "Item", "dummy-val-aaa", TestContext.Current.CancellationToken); // gitleaks:allow
            Assert.Equal(SecretVaultStatus.Denied, denied.Status);
        }

        // Actor.Module can store into its own vault and kernel can read its values
        var moduleVault = os.Get<IVault>(new NeuronId(new BrainId("system"), IVault.Type, "mod.billing"));
        using (os.Edge.Enter(os.Edge.Module("mod.billing", new BrainId("system"))))
        {
            var stored = await moduleVault.Set("billing", "Billing", "dummy-billing-aaa"); // gitleaks:allow
            Assert.Equal("secret://mod.billing/billing", stored.Reference);
        }

        using (os.Edge.EnterKernel(new BrainId("system")))
        {
            Assert.Equal(["dummy-billing-aaa"], await moduleVault.Values()); // gitleaks:allow
        }
    }

    private sealed class StubLegacyBrainKeyReader(Dictionary<string, byte[]> keys) : ILegacyBrainKeyReader
    {
        public int Calls { get; private set; }
        public bool CreatedAnyKey { get; }

        public Task<byte[]?> TryGetExistingAsync(string brainId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(keys.TryGetValue(brainId, out var key) ? key : null);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class DenyEphemeralPolicy : IBrainCapabilityPolicy
    {
        public CapabilityDecision Evaluate(BrainResidency residency, BrainCapability capability, string? resource = null, int usedTurns = 0)
            => residency is BrainResidency.Ephemeral
                ? CapabilityDecision.RequireClaim(capability, "Claim required for durable secrets.", resource)
                : CapabilityDecision.Allow(capability, resource);

        public ValueTask<CapabilityDecision> EvaluateAsync(string brainId, BrainCapability capability, string? resource = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(brainId.StartsWith("try-", StringComparison.Ordinal)
                ? CapabilityDecision.RequireClaim(capability, "Claim required for durable secrets.", resource)
                : CapabilityDecision.Allow(capability, resource));
    }
}
