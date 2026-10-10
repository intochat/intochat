using DigitalBrain.Testing.Module;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Google.Gmail;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

[Collection(GmailHostCollection.Name)]
public sealed class MailReceivedFacts(GmailHostFixture host)
{
    [Fact]
    public async Task WatchPushPublishesMailReceived()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await host.ConnectForPushAsync("owner", "direct-push");
        var gmail = brain.Get<IGmail>(ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "gmail", "direct-push"));
        await using var mail = await brain.Observe<MailReceived>(gmail, ct);
        await brain.Grains.GetGrain<IGmailInbound>(gmail.GetGrainId()).AcceptWatchPush(new("123", "user@gmail.com"));
        var received = await mail.NextAsync(ct: ct);
        Assert.Equal("user@gmail.com", received.EmailAddress);
        Assert.Equal("123", received.HistoryId);
    }
}
