using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Signals.Watch;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Client;

namespace IntoChat.OS.Tests.Unit.Authority;

public sealed class OsBrainRouteInitializerFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Provisions_each_brain_once_and_retries_after_failure()
    {
        var brain = new FakeBrain { FailNext = true };
        var initializer = new OsBrainRouteInitializer(new FakeBrains(brain));

        // First call fails (best-effort swallowed) and evicts the cached task.
        await initializer.InitializeAsync("acct-1", "brain-1", Ct);
        Assert.Equal(1, brain.CreateCalls);

        // Second call succeeds and caches the completed task.
        await initializer.InitializeAsync("acct-1", "brain-1", Ct);
        Assert.Equal(2, brain.CreateCalls);

        // Third call reuses the cached task without calling Create again.
        await initializer.InitializeAsync("acct-1", "brain-1", Ct);
        Assert.Equal(2, brain.CreateCalls);
    }

    [Fact]
    public async Task Provisions_ephemeral_brain_with_CreateEphemeral_when_registered()
    {
        var brain = new FakeBrain();
        var ephemeral = new BrainResidency.Ephemeral();
        var initializer = new OsBrainRouteInitializer(new FakeBrains(brain), new FakeResidencyRegistry("try-1234567890ab", ephemeral));

        await initializer.InitializeAsync("try-1234567890ab", "try-1234567890ab", Ct);

        Assert.Equal(0, brain.CreateCalls);
        Assert.Equal(1, brain.CreateEphemeralCalls);
    }

    private sealed class FakeBrains(IBrain brain) : IIntoChatOSBrains
    {
        public ISignalWatch Open(string brainId) => throw new NotSupportedException();
        public object Get(Type contract, string key) => brain;
        public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
    }

    private sealed class FakeResidencyRegistry(string ephemeralBrainId, BrainResidency.Ephemeral residency) : IBrainResidencyRegistry
    {
        public bool TryGetResidency(string brainId, out BrainResidency result)
        {
            if (string.Equals(brainId, ephemeralBrainId, StringComparison.Ordinal))
            {
                result = residency;
                return true;
            }
            result = new BrainResidency.Persistent("owner");
            return false;
        }

        public bool TryGetEphemeral(string brainId, out BrainResidency.Ephemeral result)
        {
            if (string.Equals(brainId, ephemeralBrainId, StringComparison.Ordinal))
            {
                result = residency;
                return true;
            }
            result = default!;
            return false;
        }

        public BrainResidencySnapshot GetSnapshot(string brainId) => throw new NotSupportedException();
        public void Touch(string brainId) { }
        public bool TryConsumeTurn(string brainId, out int remainingTurns) { remainingTurns = 0; return false; }
        public int GetTurnsUsed(string brainId) => 0;
        public void MarkPromoted(string brainId, string ownerPrincipalId) { }
    }

    private sealed class FakeBrain : IBrain
    {
        public bool FailNext { get; set; }
        public int CreateCalls { get; private set; }
        public int CreateEphemeralCalls { get; private set; }

        public Task Create(Owner owner)
        {
            CreateCalls++;
            if (FailNext)
            {
                FailNext = false;
                return Task.FromException(new InvalidOperationException("transient"));
            }
            return Task.CompletedTask;
        }

        public Task CreateEphemeral(Owner owner, BrainResidency.Ephemeral residency)
        {
            CreateEphemeralCalls++;
            return Task.CompletedTask;
        }

        public Task<BrainState> Claim(string ownerPrincipalId, string? accountId = null) => throw new NotSupportedException();
        public Task<BrainState> Read() => throw new NotSupportedException();
    }
}
