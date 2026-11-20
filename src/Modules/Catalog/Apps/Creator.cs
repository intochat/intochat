using System.Text;
using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Modules.Options;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Synapses;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Modules.Apps;

// The Creator's two agents. Author is one synchronous model call that shapes the spec or describes a unit; the Builder loop writes
// the unit. The build flow's author and draft steps call them (IDrafter), and Implement starts that flow for an app.
internal sealed class Creator(
    IServiceProvider plain,
    IOptions<AppsOptions> options,
    ILogger<Creator> logger,
    ICreatorModel model,
    SynapseCompiler? compiler = null) : IDrafter
{
    private readonly ICreatorModel models = model;
    private const int MaxAuthorRetries = 2;
    private const int MaxBuilderRounds = 12;
    private const int MaxRequestLength = 4000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string AuthorModel => string.IsNullOrWhiteSpace(options.Value.AuthorModel) ? CreatorModel.AutoModel : options.Value.AuthorModel.Trim();
    private string BuilderModel => string.IsNullOrWhiteSpace(options.Value.BuilderModel) ? CreatorModel.AutoModel : options.Value.BuilderModel.Trim();

    // With no name, the Author names the app and it exists — Drafted, in the caller's brain —
    // when this returns. With a name, it reshapes that app's spec.
    public async Task<AppSnapshot> Author(INeuronProxies brain, string brainId, string? name, string request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (request.Length > MaxRequestLength) { throw new ArgumentException($"Describe the app in at most {MaxRequestLength} characters."); }
        var target = name is null ? null : brain.Get<IApp>(IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(brainId), name).Key);
        var existing = target is null ? null : await target.Read();
        var exists = existing is { Manifest.Title.Length: > 0 };
        IApp? reporting = null;
        try
        {
            if (exists) { reporting = target; await target!.Progress("working", "Revising the specification"); }
            var prompt = existing is not null && existing.Status != AppStatus.Drafted || existing?.Spec is { Length: > 0 }
                ? $"Revise this app.\n\nCurrent specification:\n{existing!.Spec}\n\nTheir change request:\n{request.Trim()}"
                : $"Write the specification for this app.\n\nWhat the person wants:\n{request.Trim()}";
            var authored = await Authored(brainId, prompt, cancellationToken);
            var manifest = new AppManifest(authored.Title, authored.Description,
                authored.Operations ?? [], authored.Settings ?? [], authored.Slots ?? [],
                Icon: existing?.Manifest.Icon, Toolbar: existing?.Manifest.Toolbar,
                // A revision keeps what the app declared it requires, plus the capability or module id a new slot binds.
                Requires: existing?.Manifest.Requires is { } requires
                    ? requires with
                    {
                        Connectors = Merged(requires.Connectors, (authored.Slots ?? []).Where(slot => slot.Capability is null).Select(slot => slot.Connector)),
                        Capabilities = Merged(requires.Capabilities, (authored.Slots ?? []).Select(slot => slot.Capability).OfType<string>()),
                    }
                    : null);
            var app = brain.Get<IApp>(IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(brainId), name ?? authored.Name).Key);
            if (name is null)
            {
                ValidateName(authored.Name);
                await app.Create(manifest, authored.Spec);
                reporting = app;
                await app.Progress("accepted", $"Drafting {authored.Title}");
            }
            else
            {
                await app.SaveManifest(manifest, Guid.NewGuid());
                await app.SaveSpec(authored.Spec, Guid.NewGuid());
            }
            await app.Progress("completed", $"Specified {authored.Title}");
            return await app.Read();
        }
        catch (Exception error) when (reporting is not null && error is not OperationCanceledException)
        {
            // Reporting must not mask the Author's own error.
            await TryReport(reporting, "attention", error.Message, failureIsVisibleElsewhere: true);
            throw;
        }
    }

    // Flips the app to Building, starts its build flow and returns; the flow authors, drafts, verifies, deploys and installs the unit.
    public async Task<AppSnapshot> Implement(INeuronProxies brain, string appKey, string? instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var app = brain.Get<IApp>(appKey);
        var snapshot = await app.BeginBuild(instruction).WaitAsync(cancellationToken);
        var attempt = await app.CurrentBuildAttempt().WaitAsync(cancellationToken);
        // The app builds as a unit: its spec and the person's instruction are the Author's request.
        var goal = $"{snapshot.Manifest.Title}\n\n{snapshot.Spec}" + (string.IsNullOrWhiteSpace(instruction) ? "" : $"\n\nThe person asked: {instruction.Trim()}");
        var inner = new BuildInner { Goal = goal.Length <= MaxRequestLength ? goal : goal[..MaxRequestLength], AppKey = appKey, Attempt = attempt };
        var flows = SiloNeurons.Get<IFlows>(plain, IFlows.Key(BrainKey.BrainOf(appKey)).ToString());
        // BeginBuild named the attempt's flow; the flow starts under that key.
        var flowId = await app.BuildFlow().WaitAsync(cancellationToken) ?? throw new InvalidOperationException("BeginBuild named no build flow.");
        try
        {
            await flows.Start(BuildFlow.Alias, snapshot.Manifest.Title, null, JsonSerializer.Serialize(inner, BuildFlow.Json), cancellationToken,
                flowId[(flowId.LastIndexOf('/') + 1)..]);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await app.CompleteBuild(attempt, null, error.Message);
            throw;
        }
        return await app.Read().WaitAsync(cancellationToken);
    }

    // The Author for a unit: one request in, one described unit out. A reply that is not a valid unit is asked for again, twice.
    public async Task<AuthoredUnit> Author(string brainId, string request, IReadOnlyDictionary<string, string>? existing, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (request.Length > MaxRequestLength) { throw new ArgumentException($"Describe the unit in at most {MaxRequestLength} characters."); }
        var builder = new StringBuilder($"Describe the unit for this.\n\nWhat the person wants:\n{request.Trim()}");
        AppendFiles(builder, existing ?? new Dictionary<string, string>());
        var prompt = builder.ToString();
        for (var retry = 0; ; retry++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var unit = Parse<AuthoredUnit>(await Complete(brainId, AuthorModel, AgentPrompts.AuthorUnit, prompt, ct));
                Validate(unit);
                _ = KeptScenarios.Merge(existing, unit.Scenarios, request);
                return unit;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or FormatException)
            {
                if (retry < MaxAuthorRetries)
                {
                    prompt += $"\n\nYour previous reply could not be used: {error.Message} Reply with the JSON object only.";
                    continue;
                }

                throw;
            }
        }
    }

    // The Builder for an authored unit: its .cs files, checked green by check_unit, plus spec.md and scenarios.json.
    public async Task<BuildDraft> Draft(string brainId, AuthoredUnit unit, IReadOnlyDictionary<string, string>? existing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        Validate(unit);
        var task = new StringBuilder().AppendLine("Write this unit.").AppendLine().AppendLine(JsonSerializer.Serialize(unit, Indented));
        AppendFiles(task, existing ?? new Dictionary<string, string>());
        var files = await BuilderConversation(brainId, null, unit.Alias, task.ToString(), unit.Needs, unit.Scenarios, ct);
        files[UnitFiles.Spec] = unit.Spec;
        files[UnitFiles.Scenarios] = JsonSerializer.Serialize(unit.Scenarios, Indented);
        return new BuildDraft(files, null);
    }

    private static void AppendFiles(StringBuilder task, IReadOnlyDictionary<string, string> existing)
    {
        if (existing.Count == 0) { return; }
        task.AppendLine().AppendLine("Current files:");
        foreach (var (path, source) in existing) { task.AppendLine($"--- {path} ---").AppendLine(source); }
    }

    // The attention report is best effort, but its failure is logged: Debug when the caller still sees the
    // original error, Warning when the report was the only way the person would have learned of it.
    private async Task TryReport(IApp app, string state, string detail, bool failureIsVisibleElsewhere)
    {
        try
        {
            await app.Progress(state, detail);
        }
        catch (Exception failure)
        {
            if (failureIsVisibleElsewhere) { logger.LogDebug(failure, "attention report failed"); }
            else { logger.LogWarning(failure, "attention report failed for an app."); }
        }
    }

    private async Task<AuthoredApp> Authored(string brainId, string prompt, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Parse<AuthoredApp>(await Complete(brainId, AuthorModel, AgentPrompts.Author, prompt, cancellationToken));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            {
                if (retry < MaxAuthorRetries)
                {
                    prompt += $"\n\nYour previous reply could not be used: {error.Message} Reply with the JSON object only.";
                    continue;
                }

                throw;
            }
        }
    }

    // The Builder works with two tools: read_contracts and check_unit. Its final files are checked again exactly as returned; a
    // reply that is not green goes back to it with the check. Each round of an app build lands on the app's activity feed.
    private async Task<Dictionary<string, string>> BuilderConversation(string brainId, IApp? progress, string alias, string task,
        UnitNeeds? needs, IReadOnlyList<Scenario> scenarios, CancellationToken cancellationToken)
    {
        var messages = new List<AiMessage>
        {
            new("system", [new AiText(AgentPrompts.BuildUnit)]),
            new("user", [new AiText(task)]),
        };
        for (var round = 0; round < MaxBuilderRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await models.Generate(brainId, BuilderModel, new InferenceRequest(messages, Tools: BuilderTools.Definitions), cancellationToken);
            messages.AddRange(result.Messages);
            var calls = result.Messages.SelectMany(message => message.Content).OfType<AiToolCall>().ToArray();
            if (calls.Length == 0)
            {
                var reply = string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
                string? problem;
                Dictionary<string, string>? files = null;
                try
                {
                    files = new Dictionary<string, string>(Parse<BuiltUnit>(reply).Files ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                    var check = await BuilderTools.Check(Compiler, alias, files, needs, scenarios, cancellationToken);
                    problem = check.Passed ? null : "check_unit on your final files is not green. Fix these and return the corrected files JSON:\n" + JsonSerializer.Serialize(check, Json);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    problem = $"Your reply could not be used: {error.Message} Reply with {{\"files\": {{...}}}} only.";
                }
                if (problem is null) { return files!; }
                if (progress is not null) { await progress.Progress("working", $"round {round + 1}: correcting the final check"); }
                messages.Add(new AiMessage("user", [new AiText(problem)]));
                continue;
            }
            if (progress is not null)
            {
                await progress.Progress(calls.Any(call => call.Name == BuilderTools.ReadContracts) ? "reading" : "working",
                    $"round {round + 1}: {string.Join(", ", calls.Select(call => call.Name))}");
            }
            var results = new List<AiContent>();
            foreach (var call in calls) { results.Add(new AiToolResult(call.CallId, await ExecuteBuilderTool(call, alias, needs, scenarios, cancellationToken))); }
            messages.Add(new AiMessage("tool", results.ToArray()));
        }

        throw new InvalidDataException($"The Builder used {MaxBuilderRounds} rounds without producing a unit that passes check_unit.");
    }

    // The host's compiler (its unit reference set); a host without one cannot check a unit.
    private SynapseCompiler Compiler => compiler ?? plain.GetService<SynapseCompiler>() ?? throw new InvalidOperationException(SynapseStudioNeuron.Unavailable);

    private async Task<string> ExecuteBuilderTool(AiToolCall call, string alias, UnitNeeds? needs, IReadOnlyList<Scenario> scenarios, CancellationToken cancellationToken)
    {
        try
        {
            return call.Name switch
            {
                BuilderTools.ReadContracts => BuilderTools.Contracts,
                BuilderTools.CheckUnit => JsonSerializer.Serialize(await BuilderTools.Check(Compiler, alias,
                    Parse<BuilderTools.CheckArguments>(call.ArgumentsJson).Files ?? new Dictionary<string, string>(), needs, scenarios, cancellationToken), Json),
                _ => throw new ArgumentException($"Unknown tool '{call.Name}'."),
            };
        }
        // A failed tool call is the model's problem to correct, not the build's end.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<Creator>().LogSwallowed(error, $"Creator builder tool '{call.Name}' failed for unit '{alias}'.");
            return JsonSerializer.Serialize(new { error = error.Message }, Json);
        }
    }

    private Task<string> Complete(string brainId, string modelName, string system, string prompt, CancellationToken ct)
        => models.Complete(brainId, modelName, system, prompt, ct);

    // A unit's alias names its state: lowercase dotted words of letters and digits ("x.watch").
    internal static void ValidateAlias(string alias)
    {
        var parts = (alias ?? "").Split('.');
        if (parts.Length < 2 || parts.Any(part => part.Length == 0 || !char.IsAsciiLetterLower(part[0]) || part.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))))
        { throw new InvalidDataException($"'{alias}' is not a valid unit alias: lowercase dotted words such as \"x.watch\"."); }
    }

    private static void Validate(AuthoredUnit unit)
    {
        ValidateAlias(unit.Alias);
        if (string.IsNullOrWhiteSpace(unit.Title)) { throw new InvalidDataException("The unit has no title."); }
        if (string.IsNullOrWhiteSpace(unit.Spec)) { throw new InvalidDataException("The unit has no spec."); }
        if (unit.State is null || unit.Needs is null || unit.Signals is null || unit.Scenarios is null)
        { throw new InvalidDataException("The unit needs state, signals, needs and scenarios."); }
        if (!KeptScenarios.Live(unit.Scenarios).Any()) { throw new InvalidDataException("The unit has no scenarios; write at least one."); }
        foreach (var signal in unit.Signals) { ValidateAlias(signal.Alias); }
        foreach (var slot in unit.Needs.Slots ?? [])
        {
            if (string.IsNullOrEmpty(slot.Name) || slot.Name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)))
            { throw new InvalidDataException($"'{slot.Name}' is not a valid slot name: lowercase letters and digits."); }
            var capability = slot.Capability?.EndsWith("@1", StringComparison.Ordinal) == true
                ? slot.Capability[..^2]
                : slot.Capability;
            if (capability is not ("http" or "tool" or "storage" or "notifications"))
            {
                throw new InvalidDataException($"Slot '{slot.Name}' has capability '{slot.Capability}'; a slot is \"http\", \"tool\", \"storage\", or \"notifications\".");
            }
        }
        // Every scenario must read in the one grammar before the Builder sees it.
        foreach (var scenario in unit.Scenarios) { _ = ScenarioSteps.Parse(scenario); }
    }

    // A goal's unit alias for a build flow draft: "unit." and the goal's first letters and digits.
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
        { throw new InvalidDataException($"'{name}' is not a valid app name: lowercase letters, digits and dashes."); }
    }

    // Models sometimes wrap the JSON in a code fence or add prose around it; read the first complete object.
    internal static T Parse<T>(string reply)
    {
        var start = reply.IndexOf('{', StringComparison.Ordinal);
        if (start < 0) { throw new InvalidDataException("The reply contains no JSON object."); }
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(reply[start..]));
        using var document = JsonDocument.ParseValue(ref reader);
        return document.Deserialize<T>(Json) ?? throw new InvalidDataException("The reply's JSON object is empty.");
    }

    // A declared list plus what new slots bind; a list that was absent and gains nothing stays absent.
    private static string[]? Merged(string[]? declared, IEnumerable<string> bound)
    {
        var added = bound.ToArray();
        return declared is null && added.Length == 0 ? null : [.. (declared ?? []).Concat(added).Distinct(StringComparer.Ordinal)];
    }

    private sealed record AuthoredApp(string Name, string Title, string Description, string Spec,
        AppOperation[]? Operations = null, AppSetting[]? Settings = null, AppSlot[]? Slots = null, ViewRef? Entry = null);
    private sealed record BuiltUnit(IReadOnlyDictionary<string, string>? Files);
}
