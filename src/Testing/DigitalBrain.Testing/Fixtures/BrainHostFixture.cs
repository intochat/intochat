using DigitalBrain.Framework;
using Xunit;

namespace DigitalBrain.Testing;

// One host per test assembly: [assembly: AssemblyFixture(typeof(MyHost))] with MyHost : BrainHostFixture, and facts
// take MyHost in their constructor. Facts share the host, so each isolates itself in a fresh person's brain.
public abstract class BrainHostFixture : IAsyncLifetime
{
    private ITestBrain? _brain;

    protected abstract TestBrain Configure(TestBrain brain);

    public ITestBrain Brain => _brain ?? throw new InvalidOperationException("The host starts in InitializeAsync.");

    // A fresh principal, account and brain per call; the name is a prefix made unique within the host.
    public Task<Person> PersonAsync(string name = "", CancellationToken ct = default)
        => Brain.PersonAsync($"{(name.Length == 0 ? "person" : name)}-{Guid.NewGuid():N}", ct);

    // A probe subscribed as the owner, released with the host if the fact does not dispose it.
    public async Task<SignalProbe<T>> ObserveAsync<T>(INeuron source, Person owner, CancellationToken ct) where T : Signal
    {
        ArgumentNullException.ThrowIfNull(owner);
        using (owner.AsCaller()) { return await Brain.Observe<T>(source, ct).ConfigureAwait(false); }
    }

    public async ValueTask InitializeAsync() => _brain = await Configure(TestBrain.Create()).StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_brain is { } brain) { await brain.DisposeAsync().ConfigureAwait(false); }
    }
}
