using DigitalBrain.Flutter;
using DigitalBrain.Flutter.InfoBar;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.InfoBar;

public sealed class InfoBarFacts
{
    [Fact]
    public async Task ShowThenDismiss()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var bar = brain.Get<IInfoBar>("warn");
        await bar.Show("warning", "Heads up", "disk");
        Assert.True((await bar.Read()).Visible);
        await bar.Dismiss();
        Assert.False((await bar.Read()).Visible);
    }
}
