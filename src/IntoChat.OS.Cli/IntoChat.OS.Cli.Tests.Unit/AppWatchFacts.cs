namespace IntoChat.OS.Cli.Tests.Unit;

public sealed class AppWatchFacts
{
    [Fact]
    public async Task SlowBuildCannotPublishStaleEdit()
    {
        var watch = new AppWatch();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<int>();
        var first = watch.Change(async ct => { started.SetResult(); await finish.Task; return 1; }, (value, ct) => { published.Add(value); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        await started.Task;
        var second = watch.Change(ct => Task.FromResult(2), (value, ct) => { published.Add(value); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        finish.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal([2], published);
    }

    [Fact]
    public async Task CompileErrorLeavesLastGoodApp()
    {
        var watch = new AppWatch();
        var installed = 0;
        await watch.Change(ct => Task.FromResult(1), (value, ct) => { installed = value; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => watch.Change<int>(ct => throw new InvalidOperationException("compile failed"), (value, ct) => { installed = value; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.Equal(1, installed);
    }
}
