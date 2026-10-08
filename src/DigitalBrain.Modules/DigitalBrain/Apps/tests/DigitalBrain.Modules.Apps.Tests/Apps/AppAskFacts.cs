using Microsoft.AspNetCore.Http.HttpResults;

namespace DigitalBrain.Apps.Tests.Apps;

// The shell's ask waits for the app's own answer, however long the app takes, and stops when the
// request is aborted rather than at a fixed deadline.
public sealed class AppAskFacts
{
    private const string Brain = "app-ask-facts";

    [Fact]
    public async Task InvocationHistoryRetainsPendingAndTerminalOutcomesAcrossActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "history"));
        await app.Create(new AppManifest("History", "research", [new("research", "research")], [], []), "spec");
        var first = new InvokeApp(Guid.NewGuid(), "research", "First company");
        var second = new InvokeApp(Guid.NewGuid(), "research", "Second company");
        var third = new InvokeApp(Guid.NewGuid(), "research", "Queued company");
        await app.Invoke(first);
        await app.Respond(new(first.InvocationId, "Saved: First company", null));
        await app.Invoke(second);
        await app.Respond(new(second.InvocationId, null, "Source unavailable"));
        await app.Invoke(third);
        await app.Invoke(first);
        await brain.DeactivateAsync(app, ct);
        var history = await app.ReadInvocations();
        Assert.Equal([first.InvocationId, second.InvocationId, third.InvocationId], history.Select(item => item.Id));
        Assert.Equal([InvocationStatus.Completed, InvocationStatus.Failed, InvocationStatus.Pending], history.Select(item => item.Status));
        Assert.Equal("Saved: First company", history[0].Output);
        Assert.Equal("Source unavailable", history[1].Error);
        Assert.Equal("Queued company", history[2].Input);
        Assert.Equal(third.InvocationId, Assert.Single(await app.Pending()).Id);
    }

    [Fact]
    public async Task AskReturnsTheAnswerWhenTheAppResponds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "echoes", [new("echo", "echo it")], [], []), "spec");
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var invoked = await brain.SubscribeAsync<Signals.AppInvoked>(app, ct);
        var responding = Task.Run(async () =>
        {
            await foreach (var signal in invoked.ReadAllAsync(ct))
            {
                await answer.Task;
                await app.Respond(new AppResponse(signal.InvocationId, """{"said":"hi"}""", null));
                return;
            }
        }, ct);

        var asking = AppsEndpoints.Ask(app, "echo", "hi", ct);
        Assert.False(asking.IsCompleted);
        answer.SetResult();
        var result = await asking;
        await responding;

        var ok = Assert.IsType<Ok<System.Text.Json.JsonElement>>(result);
        Assert.Equal("hi", ok.Value.GetProperty("said").GetString());
    }

    [Fact]
    public async Task AnAbortedAskStopsWaitingForAnAppThatNeverAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "silent"));
        await app.Create(new AppManifest("Silent", "never answers", [new("echo", "echo it")], [], []), "spec");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var asking = AppsEndpoints.Ask(app, "echo", "hi", request.Token);
        Assert.False(asking.IsCompleted);
        await request.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
    }
}
