using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.SystemOne;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using IntoChat.OS.Modules.AI.Routing;
using DigitalBrain.Access;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Modules.Apps.Routing;

// The Jev router. It reads the deployed contract catalog, asks a System One model which contract
// and method fit the request, gates on confidence, then fills the call and invokes it. The catalog
// is the menu; confidence is the trust knob; a language model only fills key and arguments.
// It keeps its own decisions and shows them to the model, so each request is judged against state.
[Neuron("ai.router", State = "router")]
internal sealed class RouterNeuron(IServiceProvider services) : Neuron<RouterStorage>, IRouter
{
    private const int MaxOptions = 255;
    private const int MaxRecent = 16;
    private const int MaxRequestChars = 2000;
    // Never routed to: the legacy observer path (Watch/Unwatch, until 06-08 deletes it) and a plain neuron's admission hook
    // (AdmitSynapse, R4-03), which only the runtime calls before it creates a synapse (T-06-14).
    internal static readonly HashSet<string> NotCallable = ["Watch", "Unwatch", "AdmitSynapse"];
    private RouterStorage Snapshot => State;
    private string Key => Id.FullKey();
    private string Scope => Id.Brain.Value;
    private NeuronTypeSearch Capabilities() => services.GetRequiredService<NeuronTypeSearch>();

    // An app of this brain by its os key (apps are plain since 06-08).
    private IApp App(string appKey)
        => Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, appKey[(Scope.Length + 1)..]));

    public async Task<RouteResult> Route(string state, string[]? allowed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var systemone = SiloNeurons.Get<ISystemOne>(services, ISystemOne.Key(Scope));
        var result = await Decide(state, allowed, request => systemone.Ask(request, ct), ct);
        if (result.Contract.Length > 0) { await Remember(state, result.Contract, result.Method); }
        return result;
    }

    private async Task<RouteResult> Decide(string state, string[]? allowed, Func<SystemOneRequest, Task<SystemOneResult>> ask, CancellationToken ct)
    {
        if (AppsAllowed(allowed) && await AppTier(state, ask) is { } app) { return app; }
        var types = await Menu(allowed, ct);
        if (types.Length == 0) { return Unclear(); }

        var moment = AwareOfState(state);
        var criteria = types.ToDictionary(type => type.Id, type => (string?)Trim(type.Description), StringComparer.Ordinal);
        var contractQuestions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        var chunks = Chunk(criteria).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        { contractQuestions["capability_" + index] = new SystemOneQuestion.Choice("Which capability can carry out this request?", chunks[index]); }
        var contractAnswer = await ask(new SystemOneRequest(moment, contractQuestions));

        if (Best(contractAnswer, criteria.Keys) is not { } pick) { return Unclear(); }
        var (chosenId, confidence, probabilities) = pick;
        // An id outside the menu is the same unclear answer as no pick at all.
        if (types.FirstOrDefault(type => type.Id == chosenId) is not { } chosen) { return Unclear(); }
        if (chosen.Key is not null)
        {
            return new RouteResult(chosen.Contract, "Run", confidence, probabilities, chosen.Key, chosen.Tool);
        }
        var methods = chosen.Methods.Select(MethodName).Where(name => !NotCallable.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
        if (methods.Length == 0)
        {
            return new RouteResult(chosen.Contract, "", confidence, probabilities);
        }

        var methodAnswer = await ask(new SystemOneRequest(moment,
            new() { ["method"] = new SystemOneQuestion.Choice("Which method carries out this request?", methods.ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal)) }));
        var method = Best(methodAnswer, methods)?.Id ?? methods[0];
        return new RouteResult(chosen.Contract, method, confidence, probabilities);
    }

    // The first of two tiers: the brain's installed apps' operations, except the asking app's own (a request an
    // app sends is its own to answer, so the assistant's "hello" never routes back to assistant.open). A
    // "something else" answer, or one naming no offered operation, falls through to the second tier. An app
    // route carries the app and operation, so the caller has no contract to fill.
    private async Task<RouteResult?> AppTier(string state, Func<SystemOneRequest, Task<SystemOneResult>> ask)
    {
        var asker = Asker();
        var criteria = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var operation in await MenuOperations())
        {
            if (operation.App == asker) { continue; }
            criteria[operation.App + "." + operation.Name] = string.IsNullOrWhiteSpace(operation.Description) ? null : Trim(operation.Description);
        }
        if (criteria.Count == 0) { return null; }
        criteria["something else"] = "The request is not about an installed app.";
        var moment = AwareOfState(state);
        var questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        var chunks = Chunk(criteria).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        { questions["app_" + index] = new SystemOneQuestion.Choice("Which installed app operation can carry out this request?", chunks[index]); }
        var answer = await ask(new SystemOneRequest(moment, questions));
        if (Best(answer, criteria.Keys) is not { } pick || pick.Id == "something else") { return null; }
        var (chosen, confidence, probabilities) = pick;
        var split = chosen.IndexOf('.');
        if (split <= 0 || split >= chosen.Length - 1) { return null; }
        return new RouteResult(typeof(IApp).FullName!, nameof(IApp.Request), confidence, probabilities,
            App: chosen[..split], Operation: chosen[(split + 1)..]);
    }

    // Reads every installed app only until the menu exists. Later changes arrive through AppMenuHost.
    private async Task<MenuOperation[]> MenuOperations()
    {
        var catalog = services.GetRequiredService<AppOperationCatalog>();
        if (catalog.TryGet(Scope, out var cached) && await Fresh(catalog)) { return catalog.TryGet(Scope, out var current) ? current : cached; }
        List<(string App, MenuOperation[] Operations)> loaded = [];
        try
        {
            var kernel = services.GetRequiredService<IKernel>();
            await foreach (var instance in kernel.InstancesIn(Scope, kernel.TypeOf(typeof(IApp))))
            {
                if (!IntoChat.OS.Primitives.AppRef.TryParse(Id.Brain, instance.Key, out var appRef)) { continue; }
                var name = appRef.Name;
                AppSnapshot snapshot;
                try { snapshot = await App(instance.Key).Read(); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    (services.GetService<ILoggerFactory>()?.CreateLogger<RouterNeuron>() ?? OsConsoleLog.For<RouterNeuron>())
                        .LogSwallowed(error, "reading app menu snapshot");
                    return [];
                }
                if (snapshot.Manifest is null) { continue; }
                var entry = CatalogEntry.FromSnapshot(name, snapshot, Scope);
                loaded.Add((name, entry.ToMenuOperations().ToArray()));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            (services.GetService<ILoggerFactory>()?.CreateLogger<RouterNeuron>() ?? OsConsoleLog.For<RouterNeuron>())
                .LogSwallowed(error, "loading app menu operations");
            return [];
        }
        catalog.Replace(Scope, loaded);
        return loaded.SelectMany(item => item.Operations).ToArray();
    }

    // Reads into the ready menu the installed apps it has not read yet; false when the directory or an app cannot be read.
    private async Task<bool> Fresh(AppOperationCatalog catalog)
    {
        try
        {
            var kernel = services.GetRequiredService<IKernel>();
            List<string> names = [];
            await foreach (var instance in kernel.InstancesIn(Scope, kernel.TypeOf(typeof(IApp))))
            { if (IntoChat.OS.Primitives.AppRef.TryParse(Id.Brain, instance.Key, out var appRef)) { names.Add(appRef.Name); } }
            foreach (var name in catalog.Unknown(Scope, names))
            {
                var snapshot = await App(IntoChat.OS.Primitives.AppRef.Of(Id.Brain, name).Key).Read();
                var entry = CatalogEntry.FromSnapshot(name, snapshot, Scope);
                catalog.Upsert(Scope, name, entry.ToMenuOperations().ToArray());
            }
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            services.GetRequiredService<ILogger<AppOperationCatalog>>().LogSwallowed(error, "The app menu could not be refreshed; it is read again");
            return false;
        }
    }

    // The second tier's menu: the runtime tools this brain's sources registered (each a concrete instance
    // with its Key) and the contracts the caller named. The deployed catalog as a whole is never the menu,
    // and an app is reached only through its operations (the first tier), never by a raw IApp call.
    private async Task<NeuronType[]> Menu(string[]? allowed, CancellationToken ct)
        => [.. (await Capabilities().Capabilities(ct))
            .Where(type => type.Key is not null || (allowed?.Contains(type.Contract, StringComparer.Ordinal) ?? false))
            .Where(type => allowed is null || allowed.Contains(type.Contract, StringComparer.Ordinal))
            .Where(type => type.Contract != typeof(IApp).FullName)];

    // The app tier is IApp's place on the menu: it runs when the caller names no contracts, or names IApp.
    private static bool AppsAllowed(string[]? allowed) => allowed is null || allowed.Contains(typeof(IApp).FullName!, StringComparer.Ordinal);

    // The asking app, from the kernel-stamped caller (an app's behaviors call as their app's key).
    private string? Asker() => CallerRules.IsApp(Context.Caller, out var key) && IntoChat.OS.Primitives.AppRef.TryParse(Id.Brain, key, out var app) ? app.Name : null;

    // The router's own state: what it decided before, shown to the model so a follow-up is judged
    // in context. Its memory is its grain state, never a process.
    private string AwareOfState(string request)
    {
        var recent = Snapshot.Recent;
        if (recent.Length == 0) { return request; }
        var builder = new StringBuilder("Earlier requests and what they routed to:\n");
        foreach (var record in recent) { builder.AppendLine($"- \"{Clip(record.Request)}\" -> {record.Contract}.{record.Method}"); }
        builder.AppendLine().Append("Request: ").Append(Clip(request));
        return builder.ToString();
    }

    private async Task Remember(string request, string contract, string method)
    {
        var recent = Recorded(request, contract, method);
        await Save(Snapshot with { Recent = recent }, new RouterDecided(Key, recent.Length));
    }

    private RouteRecord[] Recorded(string request, string contract, string method)
    {
        var record = new RouteRecord(Clip(request), contract, method);
        return [.. Snapshot.Recent.Where(item => item.Request != record.Request || item.Contract != record.Contract).TakeLast(MaxRecent - 1), record];
    }

    // The offered option the answers' own distributions rank highest, with that option's probability as its
    // confidence. A System One answer's Value and Confidence can contradict its distribution or name an option
    // that was never offered (seen live from the local adapter), so the Value only breaks a tie; an answer
    // with no distribution keeps its Value and Confidence. Null when no answer names an offered option.
    private static Pick? Best(SystemOneResult result, ICollection<string> offered)
    {
        Pick? best = null;
        foreach (var answer in result.Answers.Values)
        {
            if (answer is not SystemOneAnswer.Choice choice) { continue; }
            var ranked = choice.Probabilities.Where(pair => offered.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
            if (choice.Probabilities.Count == 0 && offered.Contains(choice.Value)) { ranked[choice.Value] = choice.Confidence; }
            if (ranked.Count == 0) { continue; }
            var top = ranked.OrderByDescending(pair => pair.Value).ThenByDescending(pair => pair.Key == choice.Value).First();
            if (best is null || top.Value > best.Confidence) { best = new Pick(top.Key, top.Value, ranked); }
        }
        return best;
    }

    private sealed record Pick(string Id, double Confidence, Dictionary<string, double> Probabilities);

    // No offered option fits: the route is empty, and the caller
    // hands the request to the agent turn.
    private static RouteResult Unclear() => new("", "", 0, new());

    private static IEnumerable<Dictionary<string, string?>> Chunk(Dictionary<string, string?> all)
    {
        var buffer = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in all)
        {
            buffer[pair.Key] = pair.Value;
            if (buffer.Count == MaxOptions) { yield return buffer; buffer = new(StringComparer.Ordinal); }
        }
        if (buffer.Count > 0) { yield return buffer; }
    }

    private static string MethodName(string declared)
    {
        var signature = declared.Split(" — ", 2, StringSplitOptions.None)[0].Trim();
        var paren = signature.IndexOf('(');
        if (paren > 0) { signature = signature[..paren]; }
        var space = signature.LastIndexOf(' ');
        return space >= 0 ? signature[(space + 1)..] : signature;
    }

    private static string Trim(string description) => description.Length <= 200 ? description : description[..200];

    private static string Clip(string request) => request.Length <= MaxRequestChars ? request : request[..MaxRequestChars];
}
