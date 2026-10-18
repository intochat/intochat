using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Color;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Color;

public sealed class ColorFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetNormalizesHex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IColor>("accent").Set("#0a84ff");
        Assert.Equal("#0A84FF", (await brain.Get<IColor>("accent").Read()).Hex);
    }
}
