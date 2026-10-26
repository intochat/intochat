using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Testing;

public interface ITestBrain : IIntoChatOS, IAsyncDisposable
{
    T Get<T>(string id) where T : class;

    // The brain's HTTP edge, unauthenticated.
    HttpClient HttpClient { get; }

    // The host's services: in-process targets only; others throw NotSupportedException.
    IServiceProvider Services { get; }

    TestExecutionOptions Execution { get; }

    Task DeactivateAsync(IAddressable neuron, CancellationToken cancellationToken = default);

    // In-process: the host is rebuilt over the same TestHome (file grain stores and reminders on Topology=local), on the same HTTP address.
    // Neuron references and services taken before the restart belong to the stopped host: take them again after it.
    Task RestartAsync(CancellationToken cancellationToken = default);

    // Registers an account through the brain's HTTP edge; the brain owns its cookie client.
    Task<Person> PersonAsync(string name, CancellationToken cancellationToken = default);

    // The Open posture's synthetic owner: owner/owner, no cookie, the brain's plain client; throws on another posture.
    // A Person taken before RestartAsync holds the stopped host's client: take it again after a restart.
    Person Owner { get; }
}
