using DigitalBrain.Client;
using Xunit;

namespace DigitalBrain.Client.Tests.Unit;

public sealed class DigitalBrainClientFacts
{
    [Fact]
    public async Task RequiresTheScriptEdgeTheSandboxSupplies()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DigitalBrainClient.ConnectAsync([], TestContext.Current.CancellationToken));

        Assert.Contains(DigitalBrain.Client.Edge.V1.ScriptEdgeProtocol.EdgeSetting, error.Message, StringComparison.Ordinal);
    }
}
