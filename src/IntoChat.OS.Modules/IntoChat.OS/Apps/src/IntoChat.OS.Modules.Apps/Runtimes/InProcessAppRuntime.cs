using DigitalBrain.Brains;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Hosting.InProcess;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// Runs an app's compiled module type in-process. Runtime-authored code is a synapse unit, not an app program,
// so this is the only way an app runs.
internal sealed class InProcessAppRuntime(IServiceProvider services)
{
    public const string CompiledTypeKey = AppTrust.CompiledTypeSetting;

    public async Task<IReadOnlyDictionary<string, string>> Deploy(
        string appKey,
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken)
    {
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
        var host = services.GetRequiredService<InProcessAppHost>();
        await host.AssignType(appKey, type, configuration, cancellationToken);
        var views = await host.Describe(appKey, cancellationToken);
        var prefix = appKey + "/";
        return views.ToDictionary(
            v => v.Id.StartsWith(prefix, StringComparison.Ordinal) ? v.Id[prefix.Length..] : v.Id,
            v => v.Hash,
            StringComparer.Ordinal);
    }

    public Task Retire(string appKey, CancellationToken cancellationToken)
    {
        RequireAppKey(appKey);
        return services.GetRequiredService<InProcessAppHost>().Retire(appKey, cancellationToken);
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
