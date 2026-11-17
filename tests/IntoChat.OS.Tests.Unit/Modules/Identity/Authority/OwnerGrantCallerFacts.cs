using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity;

// Only the owner of the named brain, as the kernel stamped them, manages its grants. The brain is data; the stamped person is the authority.
public sealed class OwnerGrantCallerFacts : IAsyncLifetime
{
    private static readonly BrainId OwnerBrain = new("owner");
    private static readonly Grant Demo = Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes");
    private TestOS _os = null!;
    private IDirectoryStore _directory = null!;

    private EdgeAuthority Edge => _os.Edge;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        _os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<IdentityDirectoryGrain>().Register<GrantsNeuron>();
        _directory = _os.Get<IDirectoryStore>(new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main"));
        var identity = new OwnerBootstrapFacts.TestIdentityClient(_directory, _os.Edge);
        await new OwnerBootstrap(identity, new OwnerBootstrapFacts.NoOpBrainBirths(), Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), TimeProvider.System)
            .Execute(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _os.DisposeAsync();

    [Fact]
    public async Task GrantAsOwnerAllowsTheDirectOwner()
    {
        using var _ = Edge.Enter(Edge.Person("owner", "owner", OwnerBrain));
        Assert.Equal("os.notes", (await _directory.GrantAsOwner("owner", Demo)).Scope);
    }

    [Fact]
    public async Task GrantAsOwnerRefusesAnAgentActingForTheOwner()
    {
        using var _ = Edge.Enter(Edge.Agent(new Actor.Person("owner", "owner"), OwnerBrain));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _directory.GrantAsOwner("owner", Demo));
    }

    [Fact]
    public async Task GrantAsOwnerRefusesABrainThePersonDoesNotOwn()
    {
        using var _ = Edge.Enter(Edge.Person("owner", "owner", OwnerBrain));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _directory.GrantAsOwner("other", Demo with { Owner = "brain:other" }));
    }

    [Fact]
    public async Task GrantAsOwnerRefusesARelayedOwner()
    {
        using var _ = Edge.Enter(Edge.RelayedPerson("owner", "owner", OwnerBrain));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _directory.GrantAsOwner("owner", Demo));
    }

    [Fact]
    public async Task RevokeAsOwnerRefusesAnotherPerson()
    {
        using var _ = Edge.Enter(Edge.Person("mallory", "mallory", OwnerBrain));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _directory.RevokeAsOwner("owner", "grant-1"));
    }
}
