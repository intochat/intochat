using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Vault;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Identity.Local.Tests.Unit;

// The vault decides from the caller the kernel stamped. A neuron that calls it directly cannot claim to be the owner, an app or the platform.
public sealed class VaultCallerFacts : IAsyncLifetime
{
    private static readonly BrainId Brain = new("system");
    private static readonly Actor.Person Alice = new("alice", "acc-1");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "db-vault-callers-" + Guid.NewGuid().ToString("N"));
    private TestOS _os = null!;

    private EdgeAuthority Edge => _os.Edge;

    private IVault Vault => _os.Get<IVault>(new NeuronId(Brain, IVault.Type, "alice"));

    public ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        TestOS? os = null;
        services.AddLocalIdentity((_, _) => throw new InvalidOperationException("Not used."), _directory);
        _os = os = TestOS.Local(options => options.Services = services.BuildServiceProvider());
        _os.Register<VaultNeuron>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        if (System.IO.Directory.Exists(_directory)) { System.IO.Directory.Delete(_directory, recursive: true); }
    }

    private async Task<SecretRef> Stored()
    {
        using (Edge.Enter(Edge.Person("alice", "acc-1", Brain))) { return await Vault.Set("pat", "PAT", "s3cret"); }
    }

    [Fact]
    public async Task SetRefusesAnotherPerson()
    {
        using var _ = Edge.Enter(Edge.Person("bob", "acc-2", Brain));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Vault.Set("pat", "PAT", "x"));
    }

    [Fact]
    public async Task ValuesAllowsTheOwnerAndTheKernel()
    {
        await Stored();
        using (Edge.Enter(Edge.Person("alice", "acc-1", Brain))) { Assert.Equal(["s3cret"], await Vault.Values()); }
        using (Edge.EnterKernel(Brain)) { Assert.Equal(["s3cret"], await Vault.Values()); }
    }

    [Fact]
    public async Task ValuesRefusesAnotherPersonAndAnApp()
    {
        await Stored();
        using (Edge.Enter(Edge.Person("bob", "acc-2", Brain))) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Vault.Values()); }
        using (Edge.Enter(Edge.App("assistant", Alice, Brain))) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Vault.Values()); }
    }

    [Fact]
    public async Task ResolveAllowsAnAppAndRefusesAPerson()
    {
        var secret = await Stored();
        using (Edge.Enter(Edge.App("assistant", Alice, Brain))) { Assert.Equal("s3cret", await Vault.Resolve(secret)); }
        using (Edge.Enter(Edge.Person("alice", "acc-1", Brain))) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Vault.Resolve(secret)); }
    }

    [Fact]
    public async Task RemoveRefusesAnAppThatIsNotThePlatform()
    {
        await Stored();
        using var _ = Edge.Enter(Edge.App("assistant", Alice, Brain));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Vault.Remove("pat"));
    }

    [Fact]
    public async Task AnAppActingForAnotherPersonCannotResolveOrStore()
    {
        var secret = await Stored();
        using var _ = Edge.Enter(Edge.App("assistant", new Actor.Person("bob", "acc-2"), Brain));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Vault.Resolve(secret));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Vault.Set("pat", "PAT", "x"));
    }
}
