using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Sessions;
using DigitalBrain.Client.Runtime.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Microsoft.CSharp.Authoring;

public sealed class CSharpScriptSandbox(CSharpToolService tools, IBehaviorAssemblies assemblies, IServiceProvider services, IOptions<CSharpOptions> options) : IScriptSandbox
{
    public bool CanRun => tools.CanRun;
    public string AuthoringDescription => """
        - "csharp": one C# file per concern under "files" as "<name>.cs" (a single synapse is fine).
                  A synapse is a file with top-level statements and is one reaction: one `await foreach`
                  over one `brain.On<T>(source, brain.Stopping)`, acting through neurons. Shared code is a
                  file of only type declarations. "app.cs" is the app's declaration; never write it.
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

    // Rules run in the brain's sandbox session, never here: this host compiles the bundle's shared
    // classes off the grain thread (once per content), stores the library like a behavior, and asks the
    // session for verdicts. No answer in time, or no answer at all, is a rejection.
    public async Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(signal);
        if (tools.Check is not { } checker)
        {
            return bundle.Values.Any(source => CSharpScriptCheck.IsShared(source) && source.Contains("ILogic", StringComparison.Ordinal))
                ? [Verdict.Reject("no sandbox") with { Rule = RulesName }] : [];
        }
        var key = Key(bundle);
        if (!_shared.TryGetValue(key, out var shared))
        {
            shared = await Task.Run(() => checker.CompileShared(bundle), cancellationToken).ConfigureAwait(false);
            if (shared.Library is { } library) { await assemblies.Put(library, cancellationToken).ConfigureAwait(false); }
            if (_shared.Count >= SharedCapacity) { _shared.Clear(); }
            _shared[key] = shared;
        }
        if (shared.Errors.Count > 0)
        {
            return [Verdict.Reject("the rules do not compile: " + string.Join("; ", shared.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}"))) with { Rule = RulesName }];
        }
        if (!shared.HasRules || shared.Library is not { } compiled) { return []; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.RuleTimeout);
        try
        {
            var url = BrainRuntimeNeuron.Reachable(await assemblies.ReadUrl(compiled.Hash, RuleUrlLifetime, deadline.Token).ConfigureAwait(false));
            var type = signal.GetType();
            var request = new JudgeRequest(compiled.Hash, url, type.FullName + ", " + type.Assembly.GetName().Name,
                JsonSerializer.SerializeToElement(signal, type, RuntimeProtocol.Json));
            var answer = await services.GetRequiredService<SessionRunner>().Judge(brain, request, deadline.Token).ConfigureAwait(false);
            return [.. answer.Verdicts.Select(verdict => new Verdict(verdict.Allowed, verdict.Reason) { Rule = verdict.Rule })];
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested
            && error is OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return [Verdict.Reject("rule did not answer") with { Rule = RulesName }];
        }
    }

    private const string RulesName = "rules";
    private const int SharedCapacity = 256;
    private static readonly TimeSpan RuleUrlLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, SharedLibrary> _shared = new(StringComparer.Ordinal);

    private static string Key(IReadOnlyDictionary<string, string> bundle)
    {
        var text = new StringBuilder();
        foreach (var (path, source) in bundle.OrderBy(file => file.Key, StringComparer.Ordinal))
        { text.Append(path).Append((char)0).Append(source).Append((char)0); }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
