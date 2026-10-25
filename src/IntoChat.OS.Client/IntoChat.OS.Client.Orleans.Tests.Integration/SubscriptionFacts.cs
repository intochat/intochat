using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Xunit;

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

public sealed class SubscriptionFacts(ClientHost host)
{
    [Fact]
    public async Task ReadySubscribersReceiveOnePublication()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<ITestEmitter>("source");
        await using var first = await brain.SubscribeAsync<Number>(source, ct);
        await using var second = await brain.SubscribeAsync<Number>(source, ct);
        await using var a = first.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        await using var b = second.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        await source.Emit(42);
        Assert.True(await a.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.True(await b.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(42, a.Current.Value);
        Assert.Equal(42, b.Current.Value);
    }

    [Fact]
    public async Task TypeAndFullGrainIdentityIsolateSubscribers()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<ITestEmitter>("same");
        var other = brain.Get<IOtherEmitter>("same");
        await using var stream = await brain.SubscribeAsync<Number>(source, ct);
        await other.Emit(99);
        await source.EmitText("unrelated");
        await source.Emit(7);
        await using var reader = stream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(7, reader.Current.Value);
    }
}
