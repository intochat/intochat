using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.CodeBlocks;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The brain client's grants: an approved need set is remembered by hash, a deployed block's app key reaches only its bound needs,
// and its calls are kept as steps until it is released.
public sealed class ClientGrantsFacts
{
    private static IClientGrants Grants(TestOS os) => os.Get<IClientGrants>(new NeuronId(new BrainId("owner"), "client.grants", IClientGrants.DefaultKey));

    private static TestOS NewOs()
    {
        var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        return os;
    }

    [Fact]
    public async Task AnApprovedNeedSetIsRemembered()
    {
        await using var os = NewOs();
        Assert.False(await Grants(os).IsApproved("needs:a"));
        await Grants(os).Approve("needs:a", ["IRuns"]);
        await Grants(os).Approve("needs:a", ["IRuns"]);
        Assert.True(await Grants(os).IsApproved("needs:a"));
    }

    [Fact]
    public async Task ABoundBlockReachesItsNeedsAndItsCallsAreKeptUntilReleased()
    {
        await using var os = NewOs();
        var grants = Grants(os);
        await grants.Bind("codeblock-1", "codeblocks/r1", ["IntoChat.OS.Modules.Apps.Runs.IRuns"]);
        Assert.Equal(["IntoChat.OS.Modules.Apps.Runs.IRuns"], await grants.Allowed("codeblock-1"));
        Assert.Empty(await grants.Allowed("codeblock-2"));

        await grants.Record("codeblock-1", "IRuns.List", true);
        await grants.Record("codeblock-1", "IInoFiles.Write", false);
        Assert.Equal([("IRuns.List", true), ("IInoFiles.Write", false)], (await grants.Steps("codeblock-1")).Select(step => (step.Call, step.Allowed)));

        await grants.Release("codeblock-1");
        Assert.Empty(await grants.Allowed("codeblock-1"));
        Assert.Empty(await grants.Steps("codeblock-1"));
    }
}
