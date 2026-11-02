using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Programs;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Runs a csharp app as one program of its brain's runtime, which is what assigns it to the sandbox; the owner is the app key,
// so a deploy replaces whatever that app ran before. The app program ({brain}/app/{name}) keeps the record of what was built.
internal sealed class CSharpAppRuntime(IServiceProvider plain) : IAppRuntime
{
    public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
    {
        var program = await Programs(appKey).Assign(appKey, sources, configuration).WaitAsync(cancellationToken);
        await SiloNeurons.Get<IAppProgram>(plain, AppProgramKeys.For(appKey)).Assign(sources, configuration).WaitAsync(cancellationToken);
        var prefix = appKey + "/";
        return program.Behaviors.Where(behavior => behavior.Id.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(behavior => behavior.Id[prefix.Length..], behavior => behavior.Hash, StringComparer.Ordinal);
    }

    public async Task Retire(string appKey, CancellationToken cancellationToken)
    {
        await Programs(appKey).Retire(appKey).WaitAsync(cancellationToken);
        await SiloNeurons.Get<IAppProgram>(plain, AppProgramKeys.For(appKey)).Retire().WaitAsync(cancellationToken);
    }

    private IBrainPrograms Programs(string appKey)
    {
        var brain = BrainKey.BrainOf(appKey);
        if (brain.Length == 0 || !appKey.StartsWith(brain + "/apps/", StringComparison.Ordinal))
        { throw new ArgumentException("An app key is {brain}/apps/{name}.", nameof(appKey)); }
        return SiloNeurons.Get<IBrainPrograms>(plain, BrainRuntimeKeys.For(brain));
    }
}
