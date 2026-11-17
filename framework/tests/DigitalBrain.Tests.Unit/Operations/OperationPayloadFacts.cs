using System.Security.Cryptography;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using Xunit;

namespace DigitalBrain.Tests.Unit.Operations;

// An approved operation must still run after a restart: its small inline payload is kept in state, restored only when it matches
// the planned hash, and dropped once the operation is done.
public sealed class OperationPayloadFacts
{
    private static readonly byte[] Content = "hi"u8.ToArray();
    private static readonly string Hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Content));

    private sealed record PayloadPlan(byte[]? Bytes = null) : OperationPlan(false)
    {
        public override ReadOnlyMemory<byte>? Payload => Bytes;
        public override OperationPlan Persisted() => this with { Bytes = null };
        public override OperationPlan WithPayload(ReadOnlyMemory<byte> payload) => this with { Bytes = payload.ToArray() };
    }

    private static OperationState State(byte[]? payload, string? hash) =>
        new("op-1", "b1", "origin", nameof(PayloadPlan), "{}", payload?.Length, hash, OperationPhase.AwaitingApproval, false, 0d, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) { Payload = payload };

    [Fact]
    public void AKeptPayloadMatchingThePlannedHashIsRestored()
    {
        var restored = (PayloadPlan)State(Content, Hash).Restore(new PayloadPlan());
        Assert.Equal(Content, restored.Bytes);
    }

    [Fact]
    public void APayloadThatDoesNotMatchThePlannedHashIsNotRestored()
    {
        var restored = (PayloadPlan)State("other"u8.ToArray(), Hash).Restore(new PayloadPlan());
        Assert.Null(restored.Bytes);
    }

    [Fact]
    public void WithoutAKeptPayloadThePlanIsUnchanged()
    {
        var restored = (PayloadPlan)State(null, Hash).Restore(new PayloadPlan());
        Assert.Null(restored.Bytes);
    }

    [Fact]
    public async Task CompletingDropsTheKeptPayload()
    {
        var context = new FakeNeuronContext(new NeuronId(new BrainId("b1"), "runtime.operation", "op-1"));
        var neuron = new OperationNeuron();
        NeuronAttachment.Attach(neuron, context);
        await neuron.Begin(State(Content, Hash) with { Phase = OperationPhase.Running });

        var done = await neuron.Complete(new OperationResult.Succeeded());

        Assert.Null(done.Payload);
    }
}
