namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppAwaitFacts
{
    private const string Brain = "app-await-facts";

    private static async Task<(ModuleBrain Brain, IApp App)> CreatedAppWithOperation(string operation)
    {
        var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(TestContext.Current.CancellationToken);
        var app = brain.Get<IApp>(IApp.Key(Brain, Guid.NewGuid().ToString("N")));
        await app.Create(new AppManifest("Echo", "echoes", [new(operation, "echo it")], [], []), "spec");
        return (brain, app);
    }

    [Fact]
    public async Task AwaitReturnsTheAnswerWhenTheScriptResponds()
    {
        var (brain, app) = await CreatedAppWithOperation("echo");
        await using var _ = brain;
        var invocation = await app.Invoke(new(Guid.NewGuid(), "echo", "hi"));
        var waiting = app.Await(invocation.Id);
        Assert.False(waiting.IsCompleted);
        await app.Respond(new AppResponse(invocation.Id, "hi back", null));
        var done = await waiting.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(InvocationStatus.Completed, done.Status);
        Assert.Equal("hi back", done.Output);
    }

    [Fact]
    public async Task AwaitOnAFinishedInvocationReturnsAtOnce()
    {
        var (brain, app) = await CreatedAppWithOperation("echo");
        await using var _ = brain;
        var invocation = await app.Invoke(new(Guid.NewGuid(), "echo", "hi"));
        await app.Respond(new AppResponse(invocation.Id, null, "boom"));
        var done = await app.Await(invocation.Id).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(InvocationStatus.Failed, done.Status);
        Assert.Equal("boom", done.Error);
    }

    [Fact]
    public async Task AwaitOnAnUnknownInvocationIsNotFound()
    {
        var (brain, app) = await CreatedAppWithOperation("echo");
        await using var _ = brain;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => app.Await(Guid.NewGuid()));
    }
}
