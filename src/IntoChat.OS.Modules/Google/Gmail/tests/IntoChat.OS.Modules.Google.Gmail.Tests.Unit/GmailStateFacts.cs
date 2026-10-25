using Orleans.Runtime;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Unit;

public sealed class GmailStateFacts
{
    [Fact]
    public async Task FailedAggregateWritePreservesThePreviousSnapshotOnTheSameActivation()
    {
        var initial = new GmailState { GrantGeneration = 7, BoundMailbox = "fixture@example.test" };
        var storage = new FaultingState(initial) { Fail = true };
        var neuron = new GmailNeuron(storage);
        await Assert.ThrowsAsync<IOException>(() => neuron.SaveGmailStateAsync(next => next.GrantGeneration = 8));
        Assert.Same(initial, storage.State);
        Assert.Equal(7, storage.State.GrantGeneration);
        storage.Fail = false;
        await neuron.SaveGmailStateAsync(next => next.GrantGeneration = 9);
        Assert.NotSame(initial, storage.State);
        Assert.Equal(7, initial.GrantGeneration);
        Assert.Equal(9, storage.State.GrantGeneration);
        Assert.Equal("fixture@example.test", storage.State.BoundMailbox);
    }

    private sealed class FaultingState(GmailState initial) : IPersistentState<GmailState>
    {
        public GmailState State { get; set; } = initial;
        public string Etag => "";
        public bool RecordExists => true;
        public bool Fail { get; set; }
        public Task ClearStateAsync() => throw new NotSupportedException();
        public Task ReadStateAsync() => Task.CompletedTask;
        public Task WriteStateAsync() => Fail ? Task.FromException(new IOException("Fixture storage unavailable.")) : Task.CompletedTask;
    }
}
