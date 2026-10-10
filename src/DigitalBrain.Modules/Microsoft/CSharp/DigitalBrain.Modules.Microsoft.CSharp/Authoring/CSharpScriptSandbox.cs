using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Catalog;

namespace DigitalBrain.Microsoft.CSharp.Authoring;

public sealed class CSharpScriptSandbox(CSharpToolService tools, IBehaviorAssemblies assemblies) : IScriptSandbox
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

    // Rules are compiled once per bundle content and kept loaded; the vocabulary and contracts resolve
    // from the host, so ILogic<T> and the signal types are the host's own. A rule that throws rejects.
    public Task<Verdict[]> Judge(IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(signal);
        if (tools.Check is not { } checker) { return Task.FromResult<Verdict[]>([]); }
        var rules = RuleSets.Load(bundle, checker);
        var verdicts = new List<Verdict>();
        foreach (var rule in rules.Where(rule => rule.Signal.IsInstanceOfType(signal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Verdict verdict;
            try { verdict = (Verdict)rule.Validate.Invoke(Activator.CreateInstance(rule.Type), [signal])!; }
            catch (Exception error) when (error is TargetInvocationException or MissingMethodException or InvalidCastException or NullReferenceException)
            { verdict = Verdict.Reject($"the rule failed: {(error.InnerException ?? error).Message}"); }
            verdicts.Add(verdict with { Rule = rule.Type.Name });
        }
        return Task.FromResult(verdicts.ToArray());
    }

    private sealed record Rule(Type Type, Type Signal, MethodInfo Validate);

    private static class RuleSets
    {
        private const int Capacity = 64;
        private static readonly ConcurrentDictionary<string, Rule[]> Loaded = new(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<(string Key, AssemblyLoadContext? Context)> Order = new();

        public static Rule[] Load(IReadOnlyDictionary<string, string> bundle, CSharpScriptCheck checker)
        {
            var key = Key(bundle);
            if (Loaded.TryGetValue(key, out var cached)) { return cached; }
            var compiled = checker.CompileShared(bundle);
            AssemblyLoadContext? context = null;
            Rule[] rules = [];
            if (compiled is not null)
            {
                context = new AssemblyLoadContext("rules-" + key[..12], isCollectible: true);
                using var image = new MemoryStream(compiled.Assembly);
                rules = Find(context.LoadFromStream(image));
            }
            if (!Loaded.TryAdd(key, rules)) { context?.Unload(); return Loaded[key]; }
            Order.Enqueue((key, context));
            while (Order.Count > Capacity && Order.TryDequeue(out var evicted))
            {
                Loaded.TryRemove(evicted.Key, out _);
                evicted.Context?.Unload();
            }
            return rules;
        }

        private static Rule[] Find(Assembly assembly)
            => [.. assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && type.GetConstructor(Type.EmptyTypes) is not null)
                .SelectMany(type => type.GetInterfaces()
                    .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ILogic<>))
                    .Select(contract => new Rule(type, contract.GetGenericArguments()[0], contract.GetMethod(nameof(ILogic<Signal>.Validate))!)))];

        private static string Key(IReadOnlyDictionary<string, string> bundle)
        {
            var text = new StringBuilder();
            foreach (var (path, source) in bundle.OrderBy(file => file.Key, StringComparer.Ordinal))
            { text.Append(path).Append((char)0).Append(source).Append((char)0); }
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }
}
