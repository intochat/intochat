using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp.Assemblies;

namespace DigitalBrain.Microsoft.CSharp.Authoring;

public sealed class CSharpScriptSandbox(CSharpToolService tools, IBehaviorAssemblies assemblies) : IScriptSandbox
{
    public bool CanRun => tools.CanRun;
    public string AuthoringDescription => """
        - "csharp": one C# file-based app per concern under "files" as "synapses/<name>.cs" (a single
                  synapse is fine). Each synapse is one reaction: one `await foreach` over one
                  `brain.On<T>(source, brain.Stopping)`, acting through neurons. Shared code goes in
                  "lib/*.cs".
                  The app's own operations arrive as `AppRequested` signals (change only the reaction):
                    using DigitalBrain.Apps;
                    using DigitalBrain.Apps.Signals;
                    using DigitalBrain.Flutter.Text;

                    await using var brain = await DigitalBrainClient.ConnectAsync(args);
                    var app = brain.Get<IApp>(brain.Setting("App")!);
                    await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
                    { await brain.Get<IText>(app.GetPrimaryKeyString() + "/answer").Set(Answer(request.Input)); }

                    static string Answer(string input) => /* the answer */ input;
                  Never answer a request: a result is neuron state the caller reads.
                  Settings are read with brain.Setting("Name").
        """;


    public async Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
    {
        var discovery = tools.Discovery ?? throw new InvalidOperationException("This host has no script contract discovery.");
        var catalog = await discovery.Read(modules, cancellationToken);
        return new([.. catalog.Modules.Select(module => new ScriptContractModule(module.Id))],
            catalog.Contracts, catalog.Example, catalog.Truncated);
    }

    public async Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        var checker = tools.Check ?? throw new InvalidOperationException("This host has no script compiler.");
        var result = checker.Compile(files);
        foreach (var compiled in result.Assemblies.Values) { await assemblies.Put(compiled, cancellationToken); }
        return new ScriptCompilationCheck(result.Success,
            [.. result.Errors.Select(error => new ScriptCompilationDiagnostic(error.File, error.Id, error.Line, error.Message))])
        { Hashes = result.Assemblies.ToDictionary(file => file.Key, file => file.Value.Hash, StringComparer.Ordinal) };
    }
}
