using IntoChat.OS.Modules.Google.Gmail.Testing;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Connections.Signals;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailConfiguredRegistrationFacts(GmailHost host)
{
    [Fact]
    public async Task ThePlatformLoginConnectsGmailAndPublishesConnectionConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await brain.PersonAsync("login", ct);
        await using var connected = await brain.Observe<ConnectionConnected>(brain.Get<IGmail>(ConnectionKeys.For(person.BrainId, "gmail", "primary")), ct);
        await person.ConnectAsync("primary", ct);
        var signal = await connected.NextAsync(ct: ct);
        Assert.Equal("gmail", signal.ConnectorId);
        Assert.Equal("google-oauth", signal.AuthMethodId);
        Assert.Equal("user@gmail.com", signal.Account);
    }
}
