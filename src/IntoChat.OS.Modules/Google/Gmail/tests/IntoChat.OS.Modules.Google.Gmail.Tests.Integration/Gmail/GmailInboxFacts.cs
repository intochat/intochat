using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Google.Gmail.Testing;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailInboxFacts
{
    [Fact, Trait("host", "own")]
    public async Task DisappearingMessagesAreExplicitAndPreserveThePageContinuation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        google.AddMessage("present", "Still present", "Body");
        google.ListReplies.Enqueue(new(200, new
        {
            messages = new[] { new { id = "gone", threadId = "gone-thread" }, new { id = "present", threadId = "present" } },
            nextPageToken = "provider-continuation",
        }));
        google.MessageReplies["gone"] = new(404);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "disappearing");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key);
        google.ClearRequests();

        var result = await brain.Get<IGmail>(key).ReadInbox(new(2), ct);

        Assert.Null(result.Failure);
        var page = Assert.IsType<GmailInboxPage>(result.Value);
        Assert.Equal("present", Assert.Single(page.Messages).MessageId);
        Assert.Equal(["gone"], page.MissingMessageIds);
        Assert.False(string.IsNullOrEmpty(page.NextPageToken));
        Assert.NotEqual("provider-continuation", page.NextPageToken);
        Assert.Equal(3, google.Requests.Length);
    }

    [Fact, Trait("host", "own")]
    public async Task FailedMetadataHydrationFailsTheWholeReadInsteadOfReturningAPartialPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        google.AddMessage("healthy", "Healthy message", "Body");
        google.AddMessage("unavailable", "Unavailable message", "Body");
        google.MessageReplies["unavailable"] = new(503);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "hydration-failure");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key);
        google.ClearRequests();

        var result = await brain.Get<IGmail>(key).ReadInbox(new(2), ct);

        Assert.Null(result.Value);
        Assert.Equal("provider_unavailable", result.Failure?.Code);
        Assert.Single(google.Requests, request => request.Path.EndsWith("/messages/healthy", StringComparison.Ordinal));
        Assert.Equal(3, google.Requests.Count(request => request.Path.EndsWith("/messages/unavailable", StringComparison.Ordinal)));
    }

    [Fact, Trait("host", "own")]
    public async Task AContinuationCannotBeUsedByAnotherConnectionEvenForTheSameMailbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        google.AddMessage("first", "First", "Body");
        google.AddMessage("second", "Second", "Body");
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var firstKey = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "first-connection");
        var secondKey = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "second-connection");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, firstKey);
        await Grant(brain, secondKey);
        var page = (await brain.Get<IGmail>(firstKey).ReadInbox(new(1), ct)).Value!;
        Assert.NotNull(page.NextPageToken);
        google.ClearRequests();

        var result = await brain.Get<IGmail>(secondKey).ReadInbox(new(1, page.NextPageToken), ct);

        AssertInvalidContinuation(result);
        Assert.Empty(google.Requests);
    }

    [Theory, Trait("host", "own")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewGrantInvalidatesEarlierContinuationsBeforeAnyProviderRequest(bool changeMailbox)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        google.AddMessage("first", "First", "Body");
        google.AddMessage("second", "Second", "Body");
        await using var brain = await GmailBrain.StartAsync(google, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "reconnected");
        using var caller = brain.Owner.AsCaller();
        await Grant(brain, key);
        var gmail = brain.Get<IGmail>(key);
        var page = (await gmail.ReadInbox(new(1), ct)).Value!;
        Assert.NotNull(page.NextPageToken);
        if (changeMailbox) { google.EmailAddress = "another@example.test"; }
        await Grant(brain, key);
        google.ClearRequests();

        var result = await gmail.ReadInbox(new(1, page.NextPageToken), ct);

        AssertInvalidContinuation(result);
        Assert.Empty(google.Requests);
    }

    private static void AssertInvalidContinuation(GmailInboxResult result)
    {
        Assert.Null(result.Value);
        Assert.Equal("invalid_input", result.Failure?.Code);
        Assert.Equal("pageToken", Assert.Single(result.Failure!.Violations).Path);
    }

    private static Task<ConnectionView> Grant(ITestBrain brain, string key)
        => brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "read-fixture-access", "read-fixture-refresh",
            DateTimeOffset.UtcNow.AddHours(1), GmailScopes.Read, null, "{}"));
}
