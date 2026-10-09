using System.Text;
using System.Text.Json;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Routing.Signals;
using DigitalBrain.AI.SystemOne;
using DigitalBrain.Apps;
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
    : Neuron<RouterStorage>(store), IRouter, IRouterWork
{
    private const int MaxOptions = 255;
    private const int MaxRecent = 16;
    private const int MaxAsked = 64;
    private const int MaxSteps = 8;
    private const int MaxRequestChars = 2000;
    private const double Gate = 0.5;
    private static readonly HashSet<string> NotCallable = ["Watch", "Unwatch"];
    private string Key => this.GetPrimaryKeyString();
    private string Scope => NeuronAccess.ForBrainKey(Key).Scope ?? Key;
    private CapabilityCatalog Capabilities() => ServiceProvider.GetRequiredService<CapabilityCatalog>();

    public async Task Ask(RouteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.State);
        var asked = Snapshot.Asked.Where(item => item.Id != request.Id).TakeLast(MaxAsked - 1);
        await Save(Snapshot with { Asked = [.. asked, new PendingRoute(request.Id, Clip(request.State), "Routing", request.Allowed)] }, new RouterDecided(Key, Snapshot.Recent.Length));
        await this.AsReference<IRouterWork>().Decide(request.Id);
    }

    public Task<PendingRoute?> Pending(Guid id, CancellationToken ct) => Task.FromResult(Snapshot.Asked.FirstOrDefault(item => item.Id == id));

    // A one-way turn: the model takes as long as it takes and no caller waits on it, so the System One
    // HTTP call is made here directly rather than through a grain call bounded by the response timeout.
    public async Task Decide(Guid id)
    {
        if (Snapshot.Asked.FirstOrDefault(item => item.Id == id) is not { Status: "Routing" } asked) { return; }
        var http = ServiceProvider.GetRequiredService<HttpSystemOne>();
        PendingRoute settled;
        Routed routed;
        try
        {
            var result = await Decide(asked.State, asked.Allowed, request => http.Ask(request, CancellationToken.None), CancellationToken.None);
            settled = asked with { Status = "Routed", Result = result };
            routed = new Routed(id, result.Contract, result.Method, result.Confidence);
        }
        catch (Exception error)
        {
            settled = asked with { Status = "Failed", Error = error.Message };
            routed = new Routed(id, "", "", 0, error.Message);
        }
        var recent = settled.Result is { } route ? Recorded(asked.State, route.Contract, route.Method) : Snapshot.Recent;
        await Save(Snapshot with { Recent = recent, Asked = [.. Snapshot.Asked.Select(item => item.Id == id ? settled : item)] },
            new RouterDecided(Key, recent.Length), routed);
    }

    public async Task<RouteResult> Route(string state, string[]? allowed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var systemone = ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<ISystemOne>(Scope);
        var result = await Decide(state, allowed, request => systemone.Ask(request, ct), ct);
        await Remember(state, result.Contract, result.Method);
        return result;
    }

    private async Task<RouteResult> Decide(string state, string[]? allowed, Func<SystemOneRequest, Task<SystemOneResult>> ask, CancellationToken ct)
    {
        if (await AppTier(state, ask) is { } app) { return app; }
        var types = await Menu(allowed, ct);
        if (types.Length == 0) { throw new InvalidOperationException("No contracts are available to route over."); }

        var moment = AwareOfState(state);
        var criteria = types.ToDictionary(type => type.Id, type => (string?)Trim(type.Description), StringComparer.Ordinal);
        var contractQuestions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        var chunks = Chunk(criteria).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        { contractQuestions["capability_" + index] = new SystemOneQuestion.Choice("Which capability can carry out this request?", chunks[index]); }
        var contractAnswer = await ask(new SystemOneRequest(moment, contractQuestions));

        var (chosenId, confidence, probabilities) = Best(contractAnswer);
        var chosen = types.Single(type => type.Id == chosenId);
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
        var method = methodAnswer.Answers["method"] is SystemOneAnswer.Choice { Value.Length: > 0 } picked ? picked.Value : methods[0];
        return new RouteResult(chosen.Contract, method, confidence, probabilities);
    }

    // The first of two tiers: the brain's installed apps' operations. A "something else" answer falls
    // through to the second tier. An app route carries the app and operation, so Dispatch has
    // no contract to fill: it posts IApp.Request with the state as input, no language model runs.
    private async Task<RouteResult?> AppTier(string state, Func<SystemOneRequest, Task<SystemOneResult>> ask)
    {
        var factory = ServiceProvider.GetRequiredService<IGrainFactory>();
        var criteria = new Dictionary<string, string?>(StringComparer.Ordinal);
        List<string> names = [];
        // A host without the Apps module has no app tier.
        try
        {
            var kernel = ServiceProvider.GetRequiredService<IKernel>();
            await foreach (var instance in kernel.InstancesIn(Scope, kernel.TypeOf(typeof(IApp))))
            { if (IApp.NameOf(Scope, instance.Key) is { } app) { names.Add(app); } }
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
        foreach (var name in names)
        {
            AppSnapshot snapshot;
            try { snapshot = await factory.GetGrain<IApp>(IApp.Key(Scope, name)).Read(); }
            catch (Exception error) when (error is not OperationCanceledException) { continue; }
            foreach (var operation in snapshot.Manifest.Operations)
            { criteria[name + "." + operation.Name] = string.IsNullOrWhiteSpace(operation.Description) ? null : Trim(operation.Description); }
        }
        if (criteria.Count == 0) { return null; }
        criteria["something else"] = "The request is not about an installed app.";
        var moment = AwareOfState(state);
        var questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        var chunks = Chunk(criteria).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        { questions["app_" + index] = new SystemOneQuestion.Choice("Which installed app operation can carry out this request?", chunks[index]); }
        var answer = await ask(new SystemOneRequest(moment, questions));
        var (chosen, confidence, probabilities) = Best(answer);
        if (chosen == "something else") { return null; }
        var split = chosen.IndexOf('.');
        if (split <= 0 || split >= chosen.Length - 1) { return null; }
        return new RouteResult(typeof(IApp).FullName!, nameof(IApp.Request), confidence, probabilities,
            App: chosen[..split], Operation: chosen[(split + 1)..]);
    }

    // The second tier's menu: the runtime tools this brain's sources registered (each a concrete instance
    // with its Key) and the contracts the caller named. The deployed catalog as a whole is never the menu.
    private async Task<NeuronType[]> Menu(string[]? allowed, CancellationToken ct)
        => [.. (await Capabilities().Read(Scope, ct))
            .Where(type => type.Key is not null || (allowed?.Contains(type.Contract, StringComparer.Ordinal) ?? false))
            .Where(type => allowed is null || allowed.Contains(type.Contract, StringComparer.Ordinal))];

    public async Task<RouteOutcome> Dispatch(string state, string[]? allowed, CancellationToken ct)
    {
        var route = await Route(state, allowed, ct);
        if (route.Confidence < Gate || route.Method.Length == 0)
        { return new RouteOutcome(route, false, Error: "The request was not clear enough to route."); }
        if (route.App is not null)
        {
            var appKey = IApp.Key(Scope, route.App);
            try
            {
                var requestId = await ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IApp>(appKey).Request(route.Operation!, state);
                return new RouteOutcome(route, true, appKey, null, requestId.ToString());
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new RouteOutcome(route, false, appKey, null, Error: error.Message); }
        }
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
        var types = (await Menu(allowed, ct)).ToArray();
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
        if (steps.Count > 0) { await Remember(goal, steps[^1].Contract, steps[^1].Method); }
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
                + $"The brain scope is {Scope}. An app is {Scope}/apps/{{name}}; a connection is {Scope}/connection/{{connector}}/{{id}}.\n"
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
