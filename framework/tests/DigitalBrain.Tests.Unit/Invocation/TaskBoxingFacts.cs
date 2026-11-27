using System.Text.Json;
using DigitalBrain.Invocation;

namespace DigitalBrain.Tests.Unit.Invocation;

public sealed class TaskBoxingFacts
{
    public interface ISampleContract
    {
        Task Ping(CancellationToken cancellationToken = default);
        Task<string> Echo(string text, int count = 1, CancellationToken cancellationToken = default);
        ValueTask Touch(CancellationToken cancellationToken = default);
        ValueTask<int> Add(int left, int right, CancellationToken cancellationToken = default);
        string Sync(string value);
    }

    [Fact]
    public async Task UnwrapAsyncHandlesTaskTaskOfTValueTaskValueTaskOfTAndSyncValues()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await TaskBoxing.UnwrapAsync(Task.CompletedTask, typeof(Task), ct));
        Assert.Equal("hello", await TaskBoxing.UnwrapAsync(Task.FromResult("hello"), typeof(Task<string>), ct));
        Assert.Null(await TaskBoxing.UnwrapAsync(ValueTask.CompletedTask, typeof(ValueTask), ct));
        Assert.Equal(42, await TaskBoxing.UnwrapAsync(new ValueTask<int>(42), typeof(ValueTask<int>), ct));
        Assert.Equal("sync", await TaskBoxing.UnwrapAsync("sync", typeof(string), ct));
        Assert.Null(await TaskBoxing.UnwrapAsync(null, typeof(void), ct));
    }

    [Fact]
    public void DeserializeArgumentsMaterializesDefaultsAndInjectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        using var argsDoc = JsonDocument.Parse("""["hi"]""");
        var arranged = NeuronBinder.Arrange(typeof(ISampleContract), nameof(ISampleContract.Echo), argsDoc.RootElement);

        var values = NeuronBinder.DeserializeArguments(arranged, cts.Token);

        Assert.Equal(3, values.Length);
        Assert.Equal("hi", values[0]);
        Assert.Equal(1, values[1]);
        Assert.Equal(cts.Token, values[2]);
    }
}
