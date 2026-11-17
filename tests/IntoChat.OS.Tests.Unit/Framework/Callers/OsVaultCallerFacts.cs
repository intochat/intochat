using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Framework.Callers;

// The os vault decides from the caller the kernel stamped. A neuron that calls it directly cannot claim to be the owner, an app or the platform.
public sealed class OsVaultCallerFacts : IAsyncLifetime
{
    private static readonly BrainId Brain = new("system");
    private static readonly Actor.Person Alice = new("alice", "acc-1");
    private TestOS _os = null!;

    private EdgeAuthority Edge => _os.Edge;

    private IOsVault Vault => _os.Get<IOsVault>(new NeuronId(Brain, IOsVault.Type, "alice"));

    private sealed class PlainKeys : IKeyWrapper
    {
        public string Wrap(byte[] key) => Convert.ToBase64String(key);

        public byte[] Unwrap(string wrapped) => Convert.FromBase64String(wrapped);
    }

    public ValueTask InitializeAsync()
    {
        var services = new ServiceCollection().AddSingleton<IKeyWrapper, PlainKeys>().BuildServiceProvider();
        _os = TestOS.Local(options => options.Services = services);
        _os.Register<OsVaultNeuron>();
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
        using var _ = Edge.Enter(Edge.App("assistant", Alice, Brain));
        Assert.Equal("s3cret", await Vault.Resolve(secret));
    }

    [Fact]
    public async Task ResolveRefusesAPerson()
    {
        var secret = await Stored();
        using var _ = Edge.Enter(Edge.Person("alice", "acc-1", Brain));
        await Assert.ThrowsAsync<UntrustedCallerException>(() => Vault.Resolve(secret));
    }

    [Fact]
    public async Task ValuesAllowsTheOwnerAndRefusesAnApp()
    {
        await Stored();
        using (Edge.Enter(Edge.Person("alice", "acc-1", Brain))) { Assert.Equal(["s3cret"], await Vault.Values()); }
        using (Edge.Enter(Edge.App("assistant", Alice, Brain))) { await Assert.ThrowsAsync<UntrustedCallerException>(Vault.Values); }
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
        using var _ = Edge.Enter(Edge.App("assistant", new Actor.Person("bob", "acc-2"), Brain));
        await Assert.ThrowsAsync<UntrustedCallerException>(() => Vault.Resolve(secret));
        await Assert.ThrowsAnyAsync<Exception>(() => Vault.Set("pat", "PAT", "x"));
    }
}
