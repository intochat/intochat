using DigitalBrain.Framework;

namespace DigitalBrain.Testing;

public interface ITestBrain : IDigitalBrain, IBrainSubscriptions, IAsyncDisposable
{
    T Get<T>(string id) where T : class, INeuron;

    // The brain's HTTP edge, unauthenticated.
    HttpClient HttpClient { get; }

    // The host's services: in-process targets only; others throw NotSupportedException.
    IServiceProvider Services { get; }

    TestExecutionOptions Execution { get; }

    Task DeactivateAsync(IAddressable neuron, CancellationToken cancellationToken = default);

    // In-process: the host is rebuilt over the same memory stores, on the same HTTP address.
    Task RestartAsync(CancellationToken cancellationToken = default);

    Task<Person> PersonAsync(string name, CancellationToken cancellationToken = default);
}

// A registered account with its own brain (Task 8 gives it identity, a cookie client and AsCaller).
public sealed record Person(string Name);
