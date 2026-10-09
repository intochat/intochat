using System.Text;
using System.Text.Json;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Routing.Signals;
using DigitalBrain.AI.SystemOne;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Surface;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.AI.Routing;

// The Jev router. It reads the deployed contract catalog, asks a System One model which contract
// and method fit the request, gates on confidence, then fills the call and invokes it. The catalog
// is the menu; confidence is the trust knob; a language model only fills key and arguments.
// It keeps its own decisions and shows them to the model, so each request is judged against state.
[GrainType("ai.router")]
internal sealed class RouterNeuron(
    [PersistentState("router", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<RouterStorage> store)
    : Neuron<RouterStorage>(store), IRouter
{
    private const int MaxOptions = 255;
    private const int MaxRecent = 16;
    private const int MaxSteps = 8;
    private const int MaxRequestChars = 2000;
    private const double Gate = 0.5;
    private static readonly HashSet<string> NotCallable = ["Watch", "Unwatch"];
    private string Key => this.GetPrimaryKeyString();
    private string Scope => NeuronAccess.ForBrainKey(Key).Scope ?? Key;
    private CapabilityCatalog Capabilities() => ServiceProvider.GetRequiredService<CapabilityCatalog>();

    public async Task<RouteResult> Route(string state, string[]? allowed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var types = (await Capabilities().Read(Scope, ct))
            .Where(type => allowed is null || allowed.Contains(type.Contract, StringComparer.Ordinal))
            .ToArray();
        if (types.Length == 0) { throw new InvalidOperationException("No contracts are available to route over."); }

        var systemone = ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<ISystemOne>(Scope);
        var moment = AwareOfState(state);
        var criteria = types.ToDictionary(type => type.Id, type => (string?)Trim(type.Description), StringComparer.Ordinal);
        var contractQuestions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        var chunks = Chunk(criteria).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        { contractQuestions["capability_" + index] = new SystemOneQuestion.Choice("Which capability can carry out this request?", chunks[index]); }
        var contractAnswer = await systemone.Ask(new SystemOneRequest(moment, contractQuestions), ct);

        var (chosenId, confidence, probabilities) = Best(contractAnswer);
        var chosen = types.Single(type => type.Id == chosenId);
        if (chosen.Key is not null)
        {
            await Remember(state, chosen.Contract, "Run", confidence);
            return new RouteResult(chosen.Contract, "Run", confidence, probabilities, chosen.Key, chosen.Tool);
        }
        var methods = chosen.Methods.Select(MethodName).Where(name => !NotCallable.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
        if (methods.Length == 0)
        {
            await Remember(state, chosen.Contract, "", confidence);
            return new RouteResult(chosen.Contract, "", confidence, probabilities);
        }

        var methodAnswer = await systemone.Ask(new SystemOneRequest(moment,
            new() { ["method"] = new SystemOneQuestion.Choice("Which method carries out this request?", methods.ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal)) }), ct);
        var method = methodAnswer.Answers["method"] is SystemOneAnswer.Choice { Value.Length: > 0 } picked ? picked.Value : methods[0];
        await Remember(state, chosen.Contract, method, confidence);
        return new RouteResult(chosen.Contract, method, confidence, probabilities);
    }

    public async Task<RouteOutcome> Dispatch(string state, string[]? allowed, CancellationToken ct)
    {
        var route = await Route(state, allowed, ct);
        if (route.Confidence < Gate || route.Method.Length == 0)
        { return new RouteOutcome(route, false, Error: "The request was not clear enough to route."); }
        var (key, arguments) = await Fill(state, route, ct);
        try
        {
            var result = await ServiceProvider.GetRequiredService<NeuronSurface>().Call(route.Contract, key, route.Method, arguments, ct);
            return new RouteOutcome(route, true, key, arguments, result?.GetRawText());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new RouteOutcome(route, false, key, arguments, Error: error.Message); }
    }

    // The assistant: achieve a goal across several actions. Each step Jev picks the next capability
    // (or says the goal is done), a language model fills its call, it runs, and its result feeds the
    // next decision. The catalog includes ICSharpFile and the machine console/files, so the goal can
    // be reached by writing C# or by running commands and touching files on the PC.
    public async Task<GoalRun> Achieve(string goal, string[]? allowed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var types = (await Capabilities().Read(Scope, ct))
            .Where(type => allowed is null || allowed.Contains(type.Contract, StringComparer.Ordinal))
            .ToArray();
        if (types.Length == 0) { throw new InvalidOperationException("No contracts are available to route over."); }
        var byId = types.ToDictionary(type => type.Id, type => type, StringComparer.Ordinal);
        var systemone = ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<ISystemOne>(Scope);
        var surface = ServiceProvider.GetRequiredService<NeuronSurface>();
        var steps = new List<GoalStep>();
        for (var index = 0; index < MaxSteps; index++)
        {
            var moment = AwareOfState(goal, steps);
            var criteria = types.ToDictionary(type => type.Id, type => (string?)Trim(type.Description), StringComparer.Ordinal);
            criteria["finish"] = "The goal is complete, or cannot be taken further. Choose this to stop.";
            var stepAnswer = await systemone.Ask(new SystemOneRequest(moment,
                new() { ["next"] = new SystemOneQuestion.Choice("What should be done next to achieve the goal?", criteria) }), ct);
            var (chosenId, confidence, _) = Best(stepAnswer);
            if (chosenId == "finish" || confidence < Gate || !byId.TryGetValue(chosenId, out var chosen)) { break; }
            string method;
            if (chosen.Key is not null)
            {
                method = "Run";
            }
            else
            {
                var methods = chosen.Methods.Select(MethodName).Where(name => !NotCallable.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
                if (methods.Length == 0) { break; }
                var methodAnswer = await systemone.Ask(new SystemOneRequest(moment,
                    new() { ["method"] = new SystemOneQuestion.Choice("Which method carries out this step?", methods.ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal)) }), ct);
                method = methodAnswer.Answers["method"] is SystemOneAnswer.Choice { Value.Length: > 0 } picked ? picked.Value : methods[0];
            }
            try
            {
                var route = new RouteResult(chosen.Contract, method, confidence, new(), chosen.Key, chosen.Tool);
                var (key, arguments) = await Fill(moment, route, ct);
                var result = await surface.Call(chosen.Contract, key, method, arguments, ct);
                steps.Add(new GoalStep(chosen.Contract, method, key, arguments, result?.GetRawText(), null, confidence));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { steps.Add(new GoalStep(chosen.Contract, method, null, null, null, error.Message, confidence)); }
        }
        if (steps.Count > 0) { await Remember(goal, steps[^1].Contract, steps[^1].Method, steps[^1].Confidence); }
        var last = steps.LastOrDefault();
        return new GoalRun(goal, [.. steps], last?.Error ?? last?.Result);
    }

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

    // The same memory, plus everything already done for the current goal, so each step is decided
    // against the observations the earlier steps produced.
    private string AwareOfState(string goal, IReadOnlyList<GoalStep> steps)
    {
        var builder = new StringBuilder();
        var recent = Snapshot.Recent;
        if (recent.Length > 0)
        {
            builder.AppendLine("Earlier requests and what they routed to:");
            foreach (var record in recent) { builder.AppendLine($"- \"{Clip(record.Request)}\" -> {record.Contract}.{record.Method}"); }
            builder.AppendLine();
        }
        if (steps.Count > 0)
        {
            builder.AppendLine("What you have done so far for this goal:");
            foreach (var step in steps)
            { builder.AppendLine($"- {step.Contract}.{step.Method} with {Clip(step.Arguments ?? "[]")} -> {Clip(step.Error ?? step.Result ?? "")}"); }
            builder.AppendLine();
        }
        builder.Append("Goal: ").Append(Clip(goal));
        return builder.ToString();
    }

    private async Task Remember(string request, string contract, string method, double confidence)
    {
        var record = new RouteRecord(Clip(request), contract, method);
        var recent = Snapshot.Recent.Where(item => item.Request != record.Request || item.Contract != record.Contract).TakeLast(MaxRecent - 1).ToArray();
        await Save(Snapshot with { Recent = [.. recent, record] }, new RouterDecided(Key, recent.Length + 1), new Routed(Clip(request), contract, method, confidence));
    }

    // A language model fills the two things a System One model cannot produce: the grain key and the
    // argument array. This is the hybrid: Jev decides what, the language model supplies the strings.
    private async Task<(string Key, string Arguments)> Fill(string state, RouteResult route, CancellationToken ct)
    {
        var catalog = ServiceProvider.GetRequiredService<ModelCatalog>();
        var resolved = await catalog.Resolve(null, false, ct);
        using var client = await catalog.CreateClient(resolved, ct);
        var entry = route.Key is null ? null : await FindEntry(route, ct);
        var signature = entry?.Methods.FirstOrDefault() ?? Signature(route);
        var instructions = entry is null
            ? "Fill one neuron call from the user's request. Reply with JSON only: {\"key\": \"<grain key>\", \"arguments\": [ ... ]}.\n"
                + $"Contract: {route.Contract}\nMethod: {signature}\n"
                + $"The brain scope is {Scope}. An app is {Scope}/apps/{{name}}; a connection is connection/{Scope}/{{connector}}/{{id}}.\n"
                + "arguments is a JSON array, one element per method parameter, omitting CancellationToken. Use null for an omitted optional parameter."
            : "Fill the arguments for the chosen tool from the user's request. Reply with JSON only: {\"arguments\": {...}}.\n"
                + $"Contract: {route.Contract}\nTool: {route.Tool}\n{signature}\n"
                + "arguments is a JSON object matching the tool's schema, one property per argument. Use null for an omitted optional field.";
        var options = new ChatOptions { Instructions = instructions, ResponseFormat = ChatResponseFormat.Json };
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, state)], options, ct);
        using var document = JsonDocument.Parse(response.Text);
        if (route.Key is not null)
        {
            var argumentsObject = document.RootElement.TryGetProperty("arguments", out var toolArguments) && toolArguments.ValueKind == JsonValueKind.Object ? toolArguments.GetRawText() : "{}";
            return (route.Key, $"[{JsonSerializer.Serialize(route.Tool)}, {JsonSerializer.Serialize(argumentsObject)}]");
        }
        var key = document.RootElement.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String ? keyValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(key)) { throw new InvalidOperationException("The model did not supply a neuron key."); }
        var arguments = document.RootElement.TryGetProperty("arguments", out var args) ? args.GetRawText() : "[]";
        return (key!, arguments);
    }

    private async Task<NeuronType?> FindEntry(RouteResult route, CancellationToken ct)
    {
        if (route.Key is null) { return null; }
        var entries = await Capabilities().Read(Scope, ct);
        return entries.FirstOrDefault(type => type.Contract == route.Contract && type.Key == route.Key && type.Tool == route.Tool);
    }

    private string Signature(RouteResult route)
    {
        var type = ServiceProvider.GetRequiredService<ContractCatalog>().Read().SingleOrDefault(item => item.Contract == route.Contract);
        return type?.Methods.FirstOrDefault(method => MethodName(method) == route.Method) ?? route.Method;
    }

    private static (string Id, double Confidence, Dictionary<string, double> Probabilities) Best(SystemOneResult result)
    {
        SystemOneAnswer.Choice? best = null;
        var bestProbability = -1.0;
        foreach (var answer in result.Answers.Values)
        {
            if (answer is not SystemOneAnswer.Choice choice) { continue; }
            choice.Probabilities.TryGetValue(choice.Value, out var probability);
            if (probability > bestProbability) { bestProbability = probability; best = choice; }
        }
        if (best is null || best.Value.Length == 0) { throw new InvalidOperationException("The System One model did not choose a capability."); }
        return (best.Value, best.Confidence, best.Probabilities);
    }

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
