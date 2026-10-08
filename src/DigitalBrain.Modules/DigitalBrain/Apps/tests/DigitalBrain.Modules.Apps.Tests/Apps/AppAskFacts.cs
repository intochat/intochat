using Microsoft.AspNetCore.Http.HttpResults;

namespace DigitalBrain.Apps.Tests.Apps;

// The shell's ask waits for the app's own answer, however long the app takes, and stops when the
// request is aborted rather than at a fixed deadline.
public sealed class AppAskFacts
{
    private const string Brain = "app-ask-facts";

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
