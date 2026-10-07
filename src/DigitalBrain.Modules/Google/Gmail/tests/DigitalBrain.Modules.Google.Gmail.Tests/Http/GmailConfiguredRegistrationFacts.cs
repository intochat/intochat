using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Connections.Signals;
using DigitalBrain.Google.Gmail;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

[Collection(GmailHostCollection.Name)]
public sealed class GmailConfiguredRegistrationFacts(GmailHostFixture host)
{
    [Fact]
    public async Task ThePlatformLoginConnectsGmailAndPublishesConnectionConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var key = ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "gmail", "primary");
        await using var connected = await brain.Observe<ConnectionConnected>(brain.Get<IGmail>(key), ct);
        await host.ConnectForPushAsync("owner", "primary");
        var signal = await connected.NextAsync(ct: ct);
        Assert.Equal("gmail", signal.ConnectorId);
        Assert.Equal("google-oauth", signal.AuthMethodId);
        Assert.Equal("user@gmail.com", signal.Account);
    }
}
