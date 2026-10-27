using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Google.Gmail.Testing;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailReadFacts
{
    [Fact, Trait("host", "own")]
    public async Task FailedGrantCannotBePromotedByAnInheritedWriterOrReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "grant-rollback");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key);
        var before = Assert.IsType<GmailState>(faults.SnapshotFor(key));
        google.EmailAddress = "another@example.test";
        faults.FailNextGrant = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Grant(brain, key));
        google.EmailAddress = before.Account!;
        await brain.Plain().GetConnection(key).Probe();
        var after = Assert.IsType<GmailState>(faults.SnapshotFor(key));
        Assert.Equal(before.BoundMailbox, after.BoundMailbox);
        Assert.Equal(before.Account, after.Account);
        Assert.Equal(before.GrantGeneration, after.GrantGeneration);
        Assert.Equal(before.PageTokenKey, after.PageTokenKey);
        Assert.Equivalent(before.Secrets, after.Secrets, strict: true);
        var gmail = brain.Get<IGmail>(key);
        await brain.DeactivateAsync(gmail, ct);
        Assert.NotNull((await gmail.ReadInbox(new(), ct)).Value);
        Assert.Equal(before.BoundMailbox, Assert.IsType<GmailState>(faults.SnapshotFor(key)).BoundMailbox);
    }

    private static Task<ConnectionView> Grant(ITestBrain brain, string key)
        => brain.Plain().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "fixture-access", "fixture-refresh",
            DateTimeOffset.UtcNow.AddDays(7), GmailScopes.Read, null, "{}"));

    [Fact, Trait("host", "own")]
    public async Task LostGrantAcknowledgementReloadsTheCommittedMailboxBeforeAnotherTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new GmailFaultControl();
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google).ConfigureServices(faults.Configure).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "grant-acknowledgement");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key);
        var before = Assert.IsType<GmailState>(faults.SnapshotFor(key));
        google.EmailAddress = "committed@example.test";
        faults.LoseNextGrantAcknowledgement = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Grant(brain, key));
        Assert.Equal("committed@example.test", (await brain.Plain().GetConnection(key).Probe()).Account);
        var after = Assert.IsType<GmailState>(faults.SnapshotFor(key));
        Assert.Equal("committed@example.test", after.BoundMailbox);
        Assert.Equal(before.GrantGeneration + 1, after.GrantGeneration);
        Assert.NotNull((await brain.Get<IGmail>(key).ReadInbox(new(), ct)).Value);
    }
}
