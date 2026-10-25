using IntoChat.OS.Modules.Flutter.InfoBar;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.InfoBar;

public sealed class InfoBarFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ShowThenDismiss()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var bar = brain.Get<IInfoBar>("owner/warn");
        await bar.Show("warning", "Heads up", "disk");
        Assert.True((await bar.Read()).Visible);
        await bar.Dismiss();
        Assert.False((await bar.Read()).Visible);
    }
}
