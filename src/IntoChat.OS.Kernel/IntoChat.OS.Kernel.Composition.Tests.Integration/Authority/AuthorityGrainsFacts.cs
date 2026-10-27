using IntoChat.OS.Authority;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.Authority;

// 06-06c (D-04, T-06-22): Authority reaches its allow-listed raw grains only through the composition's accessor, which
// refuses any interface no allow-listed grain implements.
[Trait("host", "own")]
public sealed class AuthorityGrainsFacts
{
    [Fact]
    public void AuthorityGrainInterfacesAreAllowListedAndThePlainBrainIsNot()
    {
        Assert.True(AuthorityGrains.IsAllowListed(typeof(IBrainEntries)));
        Assert.True(AuthorityGrains.IsAllowListed(typeof(ISecrets)));
        // The brain neuron is plain (06-08): Authority reaches it through IAuthorityGrains.Brain, never as a grain.
        Assert.False(AuthorityGrains.IsAllowListed(typeof(IBrain)));
    }

    [Fact]
    public void AnyOtherInterfaceIsRefused()
    {
        Assert.False(AuthorityGrains.IsAllowListed(typeof(IStrayGrain)));
        Assert.False(AuthorityGrains.IsAllowListed(typeof(IConnection)));
        Assert.False(AuthorityGrains.IsAllowListed(typeof(IGrainWithStringKey)));
        Assert.False(AuthorityGrains.IsAllowListed(typeof(INeuron)));
    }

    [Fact(Timeout = 120_000)]
    public async Task TheSiloServesAllowListedGrainsAndRefusesOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);
        var grains = brain.Services.GetRequiredService<IAuthorityGrains>();

        Assert.NotNull(grains.Get<IBrainEntries>("owner"));
        Assert.Throws<InvalidOperationException>(() => grains.Get<IStrayGrain>("owner/l1"));
    }
}

// A grain interface no allow-list names; refused before any grain is resolved.
public interface IStrayGrain : IGrainWithStringKey { Task Ping(); }
