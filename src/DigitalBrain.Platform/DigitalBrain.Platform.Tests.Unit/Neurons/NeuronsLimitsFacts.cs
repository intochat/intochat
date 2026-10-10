using DigitalBrain.Platform.Neurons;
using DigitalBrain.Platform.Neurons.Proto;
using DigitalBrain.Testing.Module;
using DigitalBrain.Testing.Neurons;
using Grpc.Core;

namespace DigitalBrain.Platform.Tests.Unit.Neurons;

public sealed class NeuronsLimitsFacts
{
    [Fact]
    public void AnInternalErrorReachesTheClientWithoutItsMessage()
    {
        var translated = BrainCall.Translate(new InvalidOperationException("connection string secret"));
        Assert.Equal(StatusCode.Internal, translated.StatusCode);
        Assert.DoesNotContain("secret", translated.Status.Detail);
    }

    [Fact]
    public async Task AWatchNamingMoreThanTheCapIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<PulseModule>().WithHttpEdge().StartAsync(ct);
        var request = new WatchRequest { BrainId = "owner" };
        for (var i = 0; i <= NeuronsService.MaximumWatches; i++)
        {
            var source = new WatchSource { Contract = typeof(IPulse).FullName, Key = "owner/pulse-" + i };
            source.SignalTypes.Add(nameof(Pulsed));
            request.Sources.Add(source);
        }
        using var call = NeuronsCallFacts.Client(brain).Watch(request, cancellationToken: ct);
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }
}
