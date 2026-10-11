namespace DigitalBrain.Apps;

// How an app's program runs. The Apps module hands over sources and configuration; a host without a
// runtime (no csharp module) registers none and apps are data only.
public interface IAppRuntime
{
    // Compiles and runs the whole program; throws when it does not compile, leaving the previous program running.
    // Answers each synapse's assembly hash by path: the hash its run facts carry.
    Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken);
    Task Retire(string appKey, CancellationToken cancellationToken);
}
