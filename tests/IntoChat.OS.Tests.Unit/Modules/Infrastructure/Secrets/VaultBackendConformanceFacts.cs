using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Identity.Local.Vault;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Infrastructure.Secrets;
using IntoChat.OS.Modules.Infrastructure.Secrets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Infrastructure.Secrets;

/// <summary>
/// Shared Backend Conformance &amp; Security Fact Suite covering all 4 OS-04 secret store backends:
/// <list type="number">
/// <item><see cref="InMemorySecretStoreBackend"/> (default / grain state equivalent)</item>
/// <item><see cref="LocalFileSecretStoreBackend"/> (encrypted local file)</item>
/// <item><see cref="AzureKeyVaultSecretStoreBackend"/> (Azure Key Vault)</item>
/// <item><see cref="BitwardenSecretStoreBackend"/> (Bitwarden Secrets Manager)</item>
/// </list>
/// </summary>
public sealed class VaultBackendConformanceFacts : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "os04-conformance-" + Guid.NewGuid().ToString("N"));
    private readonly IMasterKeyCustody _custody = new ConfiguredMasterKeyCustody(Encoding.UTF8.GetBytes("os04-conformance-master-key-32bytes!"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    public static TheoryData<string> BackendKinds() =>
    [
        "InMemory",
        "LocalFile",
        "AzureKeyVault",
        "Bitwarden",
    ];

    private ISecretStoreBackend CreateBackend(string kind) => kind switch
    {
        "InMemory" => new InMemorySecretStoreBackend(),
        "LocalFile" => new LocalFileSecretStoreBackend(Path.Combine(_tempDir, Guid.NewGuid().ToString("N")), _custody),
        "AzureKeyVault" => new AzureKeyVaultSecretStoreBackend(
            new FakeAzureKeyVaultSecretClient(),
            _custody,
            new AzureKeyVaultSecretStoreOptions
            {
                VaultUri = new Uri("https://os04-test.vault.azure.net"),
                DeploymentId = "dep-test-01",
            }),
        "Bitwarden" => new BitwardenSecretStoreBackend(
            new FakeBitwardenSecretsClient(),
            _custody,
            new BitwardenSecretStoreOptions
            {
                OrganizationId = "org-1",
                ProjectId = "proj-1",
                AccessToken = "dummy-token-aaa", // gitleaks:allow
                DeploymentId = "dep-test-01",
            }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(BackendKinds))]
    public async Task Crud_and_multi_owner_isolation_conform_across_all_backends(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = CreateBackend(kind);

        // Absent secret returns null and Remove returns false
        Assert.Null(await backend.GetAsync("alice", "openai", ct));
        Assert.False(await backend.RemoveAsync("alice", "openai", ct));

        // Set & Get
        await backend.SetAsync("alice", "provider-a", "Provider A", "dummy-alice-aaa", ct); // gitleaks:allow
        await backend.SetAsync("alice", "provider-b", "Provider B", "dummy-alice-bbb", ct); // gitleaks:allow
        await backend.SetAsync("bob", "provider-a", "Bob Provider", "dummy-bob-ccc", ct); // gitleaks:allow

        var aliceFirst = await backend.GetAsync("alice", "provider-a", ct);
        Assert.NotNull(aliceFirst);
        Assert.Equal("provider-a", aliceFirst.Name);
        Assert.Equal("Provider A", aliceFirst.Label);
        Assert.Equal("dummy-alice-aaa", aliceFirst.Value);
        Assert.DoesNotContain("dummy-alice-aaa", aliceFirst.ToString(), StringComparison.Ordinal);

        // Multi-owner isolation
        var bobFirst = await backend.GetAsync("bob", "provider-a", ct);
        Assert.NotNull(bobFirst);
        Assert.Equal("dummy-bob-ccc", bobFirst.Value);

        var aliceValues = await backend.ListValuesAsync("alice", ct);
        Assert.Equal(2, aliceValues.Count);
        Assert.Contains("dummy-alice-aaa", aliceValues);
        Assert.Contains("dummy-alice-bbb", aliceValues);
        Assert.DoesNotContain("dummy-bob-ccc", aliceValues);

        // Overwrite existing secret
        await backend.SetAsync("alice", "provider-a", "Provider A v2", "dummy-alice-ddd", ct); // gitleaks:allow
        var updated = await backend.GetAsync("alice", "provider-a", ct);
        Assert.Equal("dummy-alice-ddd", updated?.Value);
        Assert.Equal("Provider A v2", updated?.Label);

        // Remove returns true once, false second time, leaves other owners intact
        Assert.True(await backend.RemoveAsync("alice", "provider-a", ct));
        Assert.False(await backend.RemoveAsync("alice", "provider-a", ct));
        Assert.Null(await backend.GetAsync("alice", "provider-a", ct));
        Assert.Equal("dummy-bob-ccc", (await backend.GetAsync("bob", "provider-a", ct))?.Value);
    }

    [Theory]
    [MemberData(nameof(BackendKinds))]
    public async Task VaultNeuron_works_end_to_end_over_every_backend_with_auth_and_redaction_cache(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = CreateBackend(kind);
        var services = new ServiceCollection();
        TestOS? os = null;
        services.AddSingleton(_custody);
        services.AddSingleton(backend);
        services.AddSingleton(sp => new CachingSecretValueSource(
            async (owner, token) =>
            {
                using (os!.Edge.EnterKernel(new BrainId("system")))
                {
                    var v = os.Get<IVault>(new NeuronId(new BrainId("system"), IVault.Type, owner));
                    return await v.Values();
                }
            },
            os!.Callers));

        var provider = services.BuildServiceProvider();
        await using var testOs = os = TestOS.Local(o => o.Services = provider);
        testOs.Register<VaultNeuron>();

        var systemBrain = new BrainId("system");
        var vault = testOs.Get<IVault>(new NeuronId(systemBrain, IVault.Type, "alice"));
        SecretRef stored;
        using (testOs.Edge.Enter(testOs.Edge.Person("alice", "acc-1", systemBrain)))
        {
            stored = await vault.Set("provider-c", "Provider C", "dummy-value-eee-eee"); // gitleaks:allow
            Assert.Equal("secret://alice/provider-c", stored.Reference);
            // Owner cannot call Values() directly (Kernel-only)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(vault.Values);
        }

        // App acting for alice can resolve
        using (testOs.Edge.Enter(testOs.Edge.App("assistant", new Actor.Person("alice", "acc-1"), systemBrain)))
        {
            Assert.Equal("dummy-value-eee-eee", await vault.Resolve(stored));
        }

        // Redaction cache serves secret to person caller, refuses app caller
        var cache = provider.GetRequiredService<CachingSecretValueSource>();
        using (testOs.Edge.Enter(testOs.Edge.Person("alice", "acc-1", systemBrain)))
        {
            var values = await cache.CurrentValues(ct);
            Assert.Contains("dummy-value-eee-eee", values);
        }

        using (testOs.Edge.Enter(testOs.Edge.App("sandbox", new Actor.Person("alice", "acc-1"), systemBrain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await cache.CurrentValues(ct));
        }

        // Remove cleans up backend & invalidates cache
        using (testOs.Edge.Enter(testOs.Edge.Person("alice", "acc-1", systemBrain)))
        {
            await vault.Remove("provider-c");
            var values = await cache.CurrentValues(ct);
            Assert.Empty(values);
        }
    }

    [Fact]
    public async Task External_backends_reject_cross_owner_ciphertext_substitution_via_aad()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeKv = new FakeAzureKeyVaultSecretClient();
        var options = new AzureKeyVaultSecretStoreOptions
        {
            VaultUri = new Uri("https://os04-tamper.vault.azure.net"),
            DeploymentId = "dep-tamper",
        };
        var kvBackend = new AzureKeyVaultSecretStoreBackend(fakeKv, _custody, options);
        await kvBackend.SetAsync("alice", "item", "Item", "dummy-alice-fff", ct); // gitleaks:allow

        // Copy alice's sealed ciphertext into bob's coordinate slot
        var aliceCoord = SecretCoordinate.Format("dep-tamper", "alice", "item");
        var bobCoord = SecretCoordinate.Format("dep-tamper", "bob", "item");
        var aliceItem = await fakeKv.GetSecretAsync(aliceCoord, ct);
        Assert.NotNull(aliceItem);
        await fakeKv.SetSecretAsync(bobCoord, aliceItem.Value, new Dictionary<string, string>(StringComparer.Ordinal) { ["ic-name"] = "item" }, ct);

        // Bob's AAD does not match Alice's -> AEAD authentication fails closed!
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await kvBackend.GetAsync("bob", "item", ct));
    }

    [Fact]
    public void SecretsBackendHosting_fails_closed_on_unknown_or_misconfigured_backends()
    {
        var unknownConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SecretsBackendHosting.BackendConfigKey] = "NonExistentVault" })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSecretsBackend(unknownConfig));

        var badKeyVaultConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SecretsBackendHosting.BackendConfigKey] = "AzureKeyVault",
                [AzureKeyVaultSecretStoreOptions.SectionName + ":VaultUri"] = "http://insecure.vault.azure.net",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSecretsBackend(badKeyVaultConfig));

        var badBitwardenConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SecretsBackendHosting.BackendConfigKey] = "Bitwarden",
                [BitwardenSecretStoreOptions.SectionName + ":OrganizationId"] = "org-1",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSecretsBackend(badBitwardenConfig));
    }

    [Fact]
    public async Task AzureKeyVault_recovers_soft_deleted_secret_on_409_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeKv = new FakeAzureKeyVaultSecretClient(simulateSoftDelete: true);
        var kvBackend = new AzureKeyVaultSecretStoreBackend(
            fakeKv,
            _custody,
            new AzureKeyVaultSecretStoreOptions
            {
                VaultUri = new Uri("https://os04-softdel.vault.azure.net"),
                DeploymentId = "dep-softdel",
            });

        await kvBackend.SetAsync("alice", "item", "Item", "dummy-val-one", ct); // gitleaks:allow
        Assert.True(await kvBackend.RemoveAsync("alice", "item", ct));

        // Re-setting during soft-delete retention triggers 409 -> RecoverDeletedSecretAsync -> retry succeeds.
        await kvBackend.SetAsync("alice", "item", "Item v2", "dummy-val-two", ct); // gitleaks:allow
        Assert.Equal(1, fakeKv.RecoveryCount);
        var restored = await kvBackend.GetAsync("alice", "item", ct);
        Assert.Equal("dummy-val-two", restored?.Value);
    }

    [Fact]
    public async Task Bitwarden_ListEntriesAsync_skips_malformed_json_note_without_breaking_owner_redaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeBw = new FakeBitwardenSecretsClient();
        using var bwBackend = new BitwardenSecretStoreBackend(
            fakeBw,
            _custody,
            new BitwardenSecretStoreOptions
            {
                OrganizationId = "org-1",
                ProjectId = "proj-1",
                AccessToken = "dummy-token-aaa", // gitleaks:allow
                DeploymentId = "dep-note",
            });

        await bwBackend.SetAsync("alice", "good-item", "Good", "dummy-good-value-aaa", ct); // gitleaks:allow

        // Inject a record under alice's prefix with a malformed non-JSON note
        var badKey = SecretCoordinate.OwnerPrefix("dep-note", "alice") + "0000000000000000";
        await fakeBw.UpsertAsync(badKey, "not-valid-base64", "{malformed-json", ct);

        var values = await bwBackend.ListValuesAsync("alice", ct);
        Assert.Equal(["dummy-good-value-aaa"], values);
    }

    [Fact]
    public async Task Bitwarden_HttpClient_exchanges_machine_token_via_IdentityUrl_and_caches_bearer()
    {
        var ct = TestContext.Current.CancellationToken;
        var tokenCalls = 0;
        var apiAuthHeaders = new List<string?>();
        using var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.StartsWith("https://identity.example.test/connect/token", StringComparison.Ordinal))
            {
                tokenCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"dummy-exchanged-bearer\",\"expires_in\":3600}", Encoding.UTF8, "application/json"),
                };
            }

            apiAuthHeaders.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
            };
        });

        using var http = new HttpClient(handler);
        var options = new BitwardenSecretStoreOptions
        {
            OrganizationId = "org-1",
            ProjectId = "proj-1",
            AccessToken = "0.client-one.secret-one:enc-key", // gitleaks:allow
            DeploymentId = "dep-http",
            ApiUrl = new Uri("https://api.example.test/"),
            IdentityUrl = new Uri("https://identity.example.test/"),
        };

        using var client = new HttpBitwardenSecretsClient(http, options);
        _ = await client.ListByKeyPrefixAsync("db-", ct);
        _ = await client.ListByKeyPrefixAsync("db-", ct);

        Assert.Equal(1, tokenCalls);
        Assert.Equal(["Bearer dummy-exchanged-bearer", "Bearer dummy-exchanged-bearer"], apiAuthHeaders);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class FakeAzureKeyVaultSecretClient(bool simulateSoftDelete = false) : IAzureKeyVaultSecretClient
    {
        private readonly ConcurrentDictionary<string, AzureKeyVaultSecretItem> _store = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AzureKeyVaultSecretItem> _softDeleted = new(StringComparer.Ordinal);

        public int RecoveryCount { get; private set; }

        public ValueTask<AzureKeyVaultSecretItem?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_store.TryGetValue(secretName, out var item) ? item : null);

        public ValueTask SetSecretAsync(string secretName, string value, IDictionary<string, string> tags, CancellationToken cancellationToken = default)
        {
            if (simulateSoftDelete && _softDeleted.ContainsKey(secretName))
            {
                throw new RequestFailedException(409, "Secret is currently in a deleted but recoverable state.", "Conflict", null);
            }

            _store[secretName] = new AzureKeyVaultSecretItem(secretName, value, new Dictionary<string, string>(tags, StringComparer.Ordinal));
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteSecretAsync(string secretName, CancellationToken cancellationToken = default)
        {
            if (!_store.TryRemove(secretName, out var removed))
            {
                return ValueTask.FromResult(false);
            }

            if (simulateSoftDelete)
            {
                _softDeleted[secretName] = removed;
            }

            return ValueTask.FromResult(true);
        }

        public ValueTask RecoverDeletedSecretAsync(string secretName, CancellationToken cancellationToken = default)
        {
            if (_softDeleted.TryRemove(secretName, out var recovered))
            {
                _store[secretName] = recovered;
                RecoveryCount++;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<AzureKeyVaultSecretItem>> ListSecretsByPrefixAsync(string namePrefix, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AzureKeyVaultSecretItem>>(
                [.. _store.Values.Where(v => v.Name.StartsWith(namePrefix, StringComparison.Ordinal))]);
    }

    private sealed class FakeBitwardenSecretsClient : IBitwardenSecretsClient
    {
        private readonly ConcurrentDictionary<string, BitwardenSecretRecord> _store = new(StringComparer.Ordinal);

        public ValueTask<BitwardenSecretRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_store.TryGetValue(key, out var rec) ? rec : null);

        public ValueTask<BitwardenSecretRecord> UpsertAsync(string key, string value, string note, CancellationToken cancellationToken = default)
        {
            var id = _store.TryGetValue(key, out var existing) ? existing.Id : Guid.NewGuid().ToString("D");
            var record = new BitwardenSecretRecord(id, key, value, note);
            _store[key] = record;
            return ValueTask.FromResult(record);
        }

        public ValueTask<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_store.TryRemove(key, out _));

        public ValueTask<IReadOnlyList<BitwardenSecretRecord>> ListByKeyPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<BitwardenSecretRecord>>(
                [.. _store.Values.Where(v => v.Key.StartsWith(keyPrefix, StringComparison.Ordinal))]);
    }
}
