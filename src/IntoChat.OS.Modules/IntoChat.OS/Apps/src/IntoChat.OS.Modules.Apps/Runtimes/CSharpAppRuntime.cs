using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// Runs a csharp app as one program of its brain's runtime. The app key's brain scope names the runtime.
internal sealed class CSharpAppRuntime(IServiceProvider plain) : IAppRuntime
{
    public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
    {
        var program = await Programs(appKey).Assign(appKey, sources, configuration).WaitAsync(cancellationToken);
        var prefix = appKey + "/";
        return program.Behaviors.Where(behavior => behavior.Id.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(behavior => behavior.Id[prefix.Length..], behavior => behavior.Hash, StringComparer.Ordinal);
    }

    public async Task Retire(string appKey, CancellationToken cancellationToken)
        => await Programs(appKey).Retire(appKey).WaitAsync(cancellationToken);

    private IBrainPrograms Programs(string appKey)
    {
        var brain = BrainKey.BrainOf(appKey);
        if (brain.Length == 0 || !appKey.StartsWith(brain + "/apps/", StringComparison.Ordinal))
        { throw new ArgumentException("An app key is {brain}/apps/{name}.", nameof(appKey)); }
        return SiloNeurons.Get<IBrainPrograms>(plain, BrainRuntimeKeys.For(brain));
    }
}
