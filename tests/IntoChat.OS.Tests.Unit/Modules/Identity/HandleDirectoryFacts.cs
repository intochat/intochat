using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Handles;

namespace IntoChat.OS.Authority;

public sealed class HandleDirectoryFacts
{
    [Fact]
    public async Task ClaimResolveReleaseRoundTrip()
    {
        // The system brain is platform territory: its directory, marketplace and circles are reached by platform code.
        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system-00"));
        await using var os = TestOS.Local();
        os.Register<HandleDirectoryNeuron>();
        var h = os.Get<IHandleDirectory>(IHandleDirectory.Key("Vlad"));
        Assert.True(await h.Claim("brain-1"));
        Assert.Equal("brain-1", await h.Resolve());
        Assert.False(await h.Claim("brain-2"));
        Assert.True(await h.Claim("brain-1"));
        await h.Release("brain-2");
        Assert.Equal("brain-1", await h.Resolve());
        await h.Release("brain-1");
        Assert.Null(await h.Resolve());
    }

    [Theory]
    [InlineData("vlad", true)]
    [InlineData("VLAD", true)]
    [InlineData("v", false)]
    [InlineData("-vlad", false)]
    [InlineData("vlad_h", false)]
    public void ValidatesShape(string handle, bool ok)
        => Assert.Equal(ok, IHandleDirectory.IsValid(IHandleDirectory.Normalize(handle)));

    [Fact]
    public void ShardPinsBrainId()
    {
        Assert.Equal("system-00", IHandleDirectory.BrainOf("vlad"));
        Assert.StartsWith("system-", IHandleDirectory.BrainOf("bob"));
    }
}

file static class TestOSExtensions
{
    public static T Get<T>(this TestOS os, string key) where T : class
    {
        var slash = key.IndexOf('/');
        var brain = key[..slash];
        var innerKey = key[(slash + 1)..];
        return os.Get<T>(new NeuronId(new BrainId(brain), IHandleDirectory.TypeName, innerKey));
    }
}
