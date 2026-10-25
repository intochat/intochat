namespace IntoChat.OS.Modules.Apps;

// How an app's program runs. The Apps module hands over sources and configuration; a host without a
// runtime (no csharp module) registers none and apps are data only.
public interface IAppRuntime
{
    // Compiles and runs the whole program; throws when it does not compile, leaving the previous program running.
    // Answers each synapse's assembly hash by path: the hash its run facts carry. A call that times out may
    // still land; the same program deployed again joins the deploy in flight (or answers the one applied)
    // instead of compiling again, so a caller asks again rather than waiting longer.
    Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken);
    Task Retire(string appKey, CancellationToken cancellationToken);
}

// Configuration protocol shared by the Apps owner and its runtime implementations.
public static class AppRuntimeConfiguration
{
    public static string AccountBindingKey(string slot) => "Account__" + slot;
}
