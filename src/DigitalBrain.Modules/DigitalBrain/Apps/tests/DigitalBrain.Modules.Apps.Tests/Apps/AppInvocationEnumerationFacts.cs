namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppInvocationEnumerationFacts
{
    [Fact]
    public async Task AnOperationSubscriptionRecoversOnlyItsOwnPendingInvocations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("invocation-filter-facts", "recovery"));
        await app.Create(new AppManifest("Assistant", "", [new("send", ""), new("open", "")], [], []), "spec");
        var send = await app.Invoke(new(Guid.NewGuid(), "send", "hello"));
        var open = await app.Invoke(new(Guid.NewGuid(), "open", "docked"));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var stream = app.Invocations(brain, ["open"], lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        try
        {
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(ct));
            Assert.Equal(open.Id, stream.Current.InvocationId);
            Assert.Equal("docked", stream.Current.Input);
            Assert.Contains(await app.Pending(), pending => pending.Id == send.Id);
            await app.Respond(new(open.Id, "opened", null));
            var completed = await app.Invoke(new(Guid.NewGuid(), "open", "already handled"));
            await app.Respond(new(completed.Id, "opened elsewhere", null));
            var next = stream.MoveNextAsync().AsTask();
            var liveSend = await app.Invoke(new(Guid.NewGuid(), "send", "later"));
            var liveOpen = await app.Invoke(new(Guid.NewGuid(), "open", "floating"));
            Assert.True(await next.WaitAsync(ct));
            Assert.Equal(liveOpen.Id, stream.Current.InvocationId);
            Assert.Contains(await app.Pending(), pending => pending.Id == liveSend.Id);
        }
        finally { await lifetime.CancelAsync(); }
    }
}
