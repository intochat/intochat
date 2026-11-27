using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Grants;

namespace IntoChat.OS.Authority.Identity;

// Only the owner of the named brain, as the kernel stamped them, manages its grants. The brain is data; the stamped person is the authority.
public sealed class OwnerGrantCallerFacts : IAsyncLifetime
{
    private static readonly BrainId OwnerBrain = new("owner");
    private static readonly Grant Demo = Grant.Tuple("brain:owner", "app:owner/apps/demo", "owner/apps/demo", "os.notes");
    private TestOS _os = null!;
    private IDirectory _directory = null!;

    private EdgeAuthority Edge => _os.Edge;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        services.AddSingleton<IDirectoryChangeObserver>(sp => sp.GetRequiredService<AuthorityMirror>());
        _os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<DirectoryNeuron>().Register<GrantsNeuron>();
        _directory = _os.Get<IDirectory>(new NeuronId(new BrainId("system"), IDirectory.Type, "main"));
        var identity = new OwnerBootstrapFacts.TestIdentityClient(_directory, _os.Edge);
        await new OwnerBootstrap(identity, new OwnerBootstrapFacts.NoOpBrainBirths(), Options.Create(new AuthOptions { Posture = IdentityPosture.Open }), TimeProvider.System)
            .Execute(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _os.DisposeAsync();

    [Fact]
    public async Task GrantAsOwnerAllowsTheDirectOwner()
    {
        using var _ = Edge.Enter(Edge.Person("owner", "owner", OwnerBrain));
        var grant = await _directory.GrantAsOwner("owner", Demo);
        Assert.NotNull(grant);
        Assert.Equal("os.notes", grant!.Scope);
    }

    [Fact]
    public async Task GrantAsOwnerRefusesAnAgentActingForTheOwner()
    {
        using var _ = Edge.Enter(Edge.Agent(new Actor.Person("owner", "owner"), OwnerBrain));
        Assert.Null(await _directory.GrantAsOwner("owner", Demo));
    }

    [Fact]
    public async Task GrantAsOwnerRefusesABrainThePersonDoesNotOwn()
    {
        using var _ = Edge.Enter(Edge.Person("owner", "owner", OwnerBrain));
        Assert.Null(await _directory.GrantAsOwner("other", Demo with { Owner = "brain:other" }));
    }

    [Fact]
    public async Task GrantAsOwnerRefusesARelayedOwner()
    {
        using var _ = Edge.Enter(Edge.RelayedPerson("owner", "owner", OwnerBrain));
        Assert.Null(await _directory.GrantAsOwner("owner", Demo));
    }

    [Fact]
    public async Task RevokeAsOwnerRefusesAnotherPerson()
    {
        using var _ = Edge.Enter(Edge.Person("mallory", "mallory", OwnerBrain));
        Assert.False(await _directory.RevokeGrantAsOwner("owner", "grant-1"));
    }
}
