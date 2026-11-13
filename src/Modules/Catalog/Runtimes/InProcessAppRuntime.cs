using DigitalBrain.Brains;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Apps;

// Runs an app's compiled module type in-process. Runtime-authored code is a synapse unit, not an app program,
// so this is the only way an app runs.
internal sealed class InProcessAppRuntime(IServiceProvider services)
{
    private readonly IServiceProvider _services = services;

    public const string CompiledTypeKey = AppTrust.CompiledTypeSetting;

    public Task<IReadOnlyDictionary<string, string>> Deploy(
        string appKey,
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken)
    {
        _ = _services;
        _ = cancellationToken;
        RequireAppKey(appKey);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.TryGetValue(CompiledTypeKey, out var typeName) || string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidOperationException($"App '{appKey}' names no compiled type.");
        }

        var type = Type.GetType(typeName, throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(typeName, throwOnError: false))
                .FirstOrDefault(t => t is not null)
            ?? throw new InvalidOperationException($"Compiled app type '{typeName}' could not be resolved.");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public Task Retire(string appKey, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        RequireAppKey(appKey);
        return Task.CompletedTask;
    }

    private static void RequireAppKey(string appKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        var brain = BrainKey.BrainOf(appKey);
        if (brain.Length == 0 || !appKey.StartsWith(brain + "/apps/", StringComparison.Ordinal))
        {
            throw new ArgumentException("An app key is {brain}/apps/{name}.", nameof(appKey));
        }
    }
}
