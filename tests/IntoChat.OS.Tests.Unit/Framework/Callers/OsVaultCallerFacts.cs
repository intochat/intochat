using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Identity.Local.Vault;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Framework.Callers;

// The unified vault decides from the caller the kernel stamped. A neuron that calls it directly cannot claim to be the owner, an app or the platform.
public sealed class OsVaultCallerFacts : IAsyncLifetime
{
    private static readonly BrainId Brain = new("system");
    private static readonly Actor.Person Alice = new("alice", "acc-1");

    // identity.vault is cluster-internal: callers reach it relayed by a neuron (the secrets port), never directly from the edge.
    private static readonly NeuronId Relay = new(Brain, "identity.secrets", "alice");
    private TestOS _os = null!;

    private EdgeAuthority Edge => _os.Edge;

    private IVault Vault => _os.Get<IVault>(new NeuronId(Brain, IVault.Type, "alice"));

    public ValueTask InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton<IMasterKeyCustody>(new ConfiguredMasterKeyCustody(Encoding.UTF8.GetBytes("test-master-key-for-os-vault-caller-facts")))
            .BuildServiceProvider();
        _os = TestOS.Local(options => options.Services = services);
        _os.Register<VaultNeuron>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _os.DisposeAsync();

    private async Task<SecretRef> Stored()
    {
        using (Edge.Enter(Edge.Person("alice", "acc-1", Brain))) { return await Vault.Set("pat", "PAT", "s3cret"); }
    }

    [Fact]
    public async Task ResolveAllowsTheAssistantAppStampedByMcp()
    {
        var secret = await Stored();
        using var _ = Edge.Enter(Edge.OnBehalfOf(Alice, Brain, viaApp: "assistant", via: Relay));
        Assert.Equal("s3cret", await Vault.Resolve(secret));
    }

    [Fact]
    public async Task ResolveRefusesAPerson()
    {
        var secret = await Stored();
        using var _ = Edge.Enter(Edge.RelayedPerson("alice", "acc-1", Brain));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Vault.Resolve(secret));
    }

    [Fact]
    public async Task ValuesServeOnlyTheKernelsRedactor()
    {
        await Stored();
        using (Edge.EnterKernel(Brain)) { Assert.Equal(["s3cret"], await Vault.Values()); }
        using (Edge.Enter(Edge.Person("alice", "acc-1", Brain))) { await Assert.ThrowsAsync<UnauthorizedAccessException>(Vault.Values); }
        using (Edge.Enter(Edge.RelayedPerson("alice", "acc-1", Brain))) { await Assert.ThrowsAsync<UnauthorizedAccessException>(Vault.Values); }
        using (Edge.Enter(Edge.OnBehalfOf(Alice, Brain, viaApp: "assistant", via: Relay))) { await Assert.ThrowsAsync<UnauthorizedAccessException>(Vault.Values); }
    }

    [Fact]
    public async Task SetAndRemoveRefuseAnotherPerson()
    {
        await Stored();
        using var _ = Edge.Enter(Edge.Person("bob", "acc-2", Brain));
        await Assert.ThrowsAnyAsync<Exception>(() => Vault.Set("pat", "PAT", "x"));
        await Assert.ThrowsAnyAsync<Exception>(() => Vault.Remove("pat"));
    }

    [Fact]
    public async Task AnAppActingForAnotherPersonCannotResolveOrStore()
    {
        var secret = await Stored();
        using var _ = Edge.Enter(Edge.OnBehalfOf(new Actor.Person("bob", "acc-2"), Brain, viaApp: "assistant", via: Relay));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Vault.Resolve(secret));
        await Assert.ThrowsAnyAsync<Exception>(() => Vault.Set("pat", "PAT", "x"));
    }
}
