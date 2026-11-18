using IntoChat.OS.Tests;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Handles;

namespace IntoChat.OS.Authority.Tests.Flows;

public sealed class HandleFlowFacts(IdentityFixture fixture) : IClassFixture<IdentityFixture>
{
    public static TheoryData<NeuronRuntimeKind> Kinds => IdentityFixture.Kinds;

    [Theory, MemberData(nameof(Kinds))]
    public async Task AHandleClaimedInOneBrainResolvesFromAnother(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var handle = IHandleDirectory.Normalize("alice-" + kind.ToString().ToLowerInvariant());
        var id = runtime.Id(IHandleDirectory.TypeName, "handles/" + handle);

        var claimer = runtime.Get<IHandleDirectory>(id);
        Assert.True(await claimer.Claim("brain-alice"));
        Assert.False(await claimer.Claim("brain-bob"));

        var resolver = runtime.Get<IHandleDirectory>(id);
        Assert.Equal("brain-alice", await resolver.Resolve());
    }
}
