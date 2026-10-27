using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Operations;
using IntoChat.OS.Authority.Neurons.Proto;
using IntoChat.OS.Confirmation;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Grpc.Core;

namespace IntoChat.OS.Authority.Tests.Integration.Neurons;

[Trait("host", "own")]
public sealed class ConfirmationWatchFacts
{
    [Fact]
    public async Task TheHostPermitAuthorityCannotBeCalledThroughTheNeuronEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var request = new CallRequest
        {
            BrainId = "owner",
            Contract = "DigitalBrain.Runtime.Orleans.Operations.IStorageWriteGrants",
            Key = "owner",
            Method = "Issue",
            Arguments = ByteString.CopyFromUtf8("[\"owner/integrations/windows/disk\",\"person\",\"plan\"]"),
        };
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await NeuronsCallFacts.Client(brain).CallAsync(request, cancellationToken: ct));
        Assert.Equal(StatusCode.InvalidArgument, denied.StatusCode);
    }

    [Fact]
    public async Task TheShellWatchesRequestedAndResolvedSignalsOnTheRuntimeInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var request = new WatchRequest { BrainId = "owner" };
        var source = new WatchSource { Contract = typeof(IConfirmationInbox).FullName, Key = IConfirmationInbox.Key("owner") };
        source.SignalTypes.AddRange([nameof(ConfirmationRequested), nameof(ConfirmationResolved)]);
        request.Sources.Add(source);
        using var call = NeuronsCallFacts.Client(brain).Watch(request, cancellationToken: ct);
        var next = call.ResponseStream.MoveNext(ct);
        var contexts = brain.Services.GetRequiredService<IConfirmationContexts>();
        var plan = new ConfirmationPlan("cf-watch", ConfirmationAction.Delete, true, "Delete report?", "This removes report.", "Delete", "Keep", "report");
        Task<ConfirmationResult> answer;
        using (brain.Owner.AsApp("owner/apps/files")) { answer = contexts.For(new("owner"), "owner/files/main").Confirm(plan, ct); }
        try
        {
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(15), ct));
            Assert.Equal(nameof(ConfirmationRequested), call.ResponseStream.Current.Type);
            using (brain.Owner.AsCaller()) { await brain.Get<IConfirmationInbox>(source.Key).Resolve(plan.Id, false); }
            Assert.True(await call.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(15), ct));
            Assert.Equal(nameof(ConfirmationResolved), call.ResponseStream.Current.Type);
            Assert.Equal(ConfirmationOutcome.Denied, (await answer).Outcome);
        }
        finally { using (brain.Owner.AsCaller()) { await brain.Get<IConfirmationInbox>(source.Key).Resolve(plan.Id, false); } }
    }
}
