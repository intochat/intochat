using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Programs;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Runs a csharp app as a dedicated app program ({brain}/app/{name}).
internal sealed class CSharpAppRuntime(IServiceProvider plain) : IAppRuntime
{
    public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
    {
        var appProgramKey = AppProgramKeys.For(appKey);
        var program = await SiloNeurons.Get<IAppProgram>(plain, appProgramKey)
            .Assign(sources, configuration)
            .WaitAsync(cancellationToken);
        var prefix = appProgramKey + "/";
        return program.Behaviors.Where(behavior => behavior.Id.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(behavior => behavior.Id[prefix.Length..], behavior => behavior.Hash, StringComparer.Ordinal);
    }

    public async Task Retire(string appKey, CancellationToken cancellationToken)
        => await SiloNeurons.Get<IAppProgram>(plain, AppProgramKeys.For(appKey))
            .Retire()
            .WaitAsync(cancellationToken);
}
