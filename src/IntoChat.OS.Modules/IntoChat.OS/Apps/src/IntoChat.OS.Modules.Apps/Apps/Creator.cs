using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Modules.Options;
using System.Text;
using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Apps;

// The Creator's two agents. Author is one synchronous model call that shapes the spec; the Builder loop is the build flow's
// draft step (IDrafter), and Implement starts that flow.
internal sealed class Creator(
    IServiceProvider plain,
    NeuronTypeSearch search,
    IOptions<AppsOptions> options,
    AppAuthoringPolicy policy,
    ILogger<Creator> logger,
    IScriptSandbox? csharp = null) : IDrafter
{
    private const int MaxAuthorRetries = 2;
    private const int MaxBuilderRounds = 12;
    private const int MaxRequestLength = 4000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string AuthorModel => options.Value.AuthorModel ?? nameof(IGpt56Luna);
    private string BuilderModel => options.Value.BuilderModel ?? nameof(IGpt56Luna);

    // With no name, the Author names the app and it exists — Drafted, in the caller's brain —
    // when this returns. With a name, it reshapes that app's spec.
    public async Task<AppSnapshot> Author(INeuronProxies brain, string brainId, string? name, string request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (request.Length > MaxRequestLength) { throw new ArgumentException($"Describe the app in at most {MaxRequestLength} characters."); }
        var target = name is null ? null : brain.Get<IApp>(IApp.Key(brainId, name));
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
            var app = brain.Get<IApp>(IApp.Key(brainId, name ?? authored.Name));
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

    // Flips the app to Building, starts its build flow and returns; the flow drafts, checks and lands the files.
    public async Task<AppSnapshot> Implement(INeuronProxies brain, string appKey, string? instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        policy.RequireSandbox();
        var app = brain.Get<IApp>(appKey);
        var snapshot = await app.BeginBuild(instruction).WaitAsync(cancellationToken);
        var attempt = await app.CurrentBuildAttempt().WaitAsync(cancellationToken);
        var inner = new BuildInner { Kind = BuildInner.App, Goal = snapshot.Manifest.Title, AppKey = appKey, Instruction = instruction, Attempt = attempt };
        var flows = SiloNeurons.Get<IFlows>(plain, IFlows.Key(BrainKey.BrainOf(appKey)).ToString());
        try
        {
            var flowId = await flows.Start(BuildFlow.Alias, snapshot.Manifest.Title, null, JsonSerializer.Serialize(inner, BuildFlow.Json), cancellationToken);
            await app.BuildFlowStarted(flowId).WaitAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await app.CompleteBuild(attempt, null, error.Message);
            throw;
        }
        return await app.Read().WaitAsync(cancellationToken);
    }

    public async Task<BuildDraft> Draft(BuildRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var task = new StringBuilder();
        IReadOnlyDictionary<string, string> existing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Snapshot is { } snapshot)
        {
            task.AppendLine($"App: {snapshot.Manifest.Title}")
                .AppendLine().AppendLine("Specification:").AppendLine(snapshot.Spec);
            task.AppendLine().AppendLine("Manifest:").AppendLine(JsonSerializer.Serialize(snapshot.Manifest, Json));
            existing = snapshot.Files;
        }
        else
        {
            task.AppendLine("Write a headless synapse unit (synapse source files only, no program files) for:").AppendLine(request.Goal.Trim());
        }
        if (!string.IsNullOrWhiteSpace(request.Instruction)) { task.AppendLine().AppendLine("The person asked:").AppendLine(request.Instruction.Trim()); }
        if (existing.Count > 0)
        {
            task.AppendLine().AppendLine("Current files:");
            foreach (var (path, source) in existing) { task.AppendLine($"--- {path} ---").AppendLine(source); }
        }
        // An app build lands each Builder round on the app's activity feed.
        var progress = request.AppKey is { } appKey ? SiloNeurons.Get<IApp>(plain, appKey) : null;
        var built = Parse<BuiltApp>(await BuilderConversation(request.Brain, progress, task.ToString(), existing, ct));
        var files = new Dictionary<string, string>(built.Files ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (files.Count == 0) { throw new InvalidDataException("the Builder produced no files"); }
        if (request.Snapshot is not { Manifest.Headless: false } app) { return new BuildDraft(files, null); }
        // The code is the operation list; the Builder's and the authored lists only describe what the code has.
        var derived = AppShape.Operations(files);
        var manifest = app.Manifest with
        {
            Operations = derived.Length > 0
                ? AppShape.Merge(derived, (built.Operations ?? []).Concat(app.Manifest.Operations))
                : built.Operations is { Length: > 0 } ? built.Operations : app.Manifest.Operations,
            Settings = built.Settings is { Count: > 0 }
                    ? [.. built.Settings.Select(setting => new AppSetting(setting.Name, setting.Description ?? "", setting.Default ?? ""))]
                    : app.Manifest.Settings,
        };
        return new BuildDraft(files, manifest);
    }

    // The program an app build lands (the existing files with the draft over them), checked as the landing deploy will; null when it
    // has no program files.
    internal async Task<ScriptCompilationCheck?> CheckProgram(IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string> draft, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var (path, source) in draft) { files[path] = source; }
        var program = files.Where(file => AppFiles.IsProgram(file.Key)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        return program.Count == 0 ? null : await Checked(program, ct);
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
            if (failureIsVisibleElsewhere) { logger.LogSwallowed(failure, "attention report failed"); }
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
                return Parse<AuthoredApp>(await CreatorModel.Complete(plain, brainId, AuthorModel, AgentPrompts.Author, prompt, cancellationToken));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException && retry < MaxAuthorRetries)
            {
                prompt += $"\n\nYour previous reply could not be used: {error.Message} Reply with the JSON object only.";
            }
        }
    }

    // The Builder works with tools: it searches the contract catalog, reads details and
    // compile-checks its C# before answering. Each round of an app build lands on the app's activity feed.
    private async Task<string> BuilderConversation(string brainId, IApp? progress, string task, IReadOnlyDictionary<string, string> existingFiles, CancellationToken cancellationToken)
    {
        var messages = new List<AiMessage>
        {
            new("system", [new AiText(AgentPrompts.Builder + (csharp is null ? "" : "\n\n" + csharp.AuthoringDescription))]),
            new("user", [new AiText(task)]),
        };
        for (var round = 0; round < MaxBuilderRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CreatorModel.Generate(plain, brainId, BuilderModel,
                new InferenceRequest(messages, Tools: BuilderTools.Definitions), cancellationToken);
            messages.AddRange(result.Messages);
            var calls = result.Messages.SelectMany(message => message.Content).OfType<AiToolCall>().ToArray();
            if (calls.Length == 0)
            {
                var reply = string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
                var built = Parse<BuiltApp>(reply);
                // CompleteBuild merges the files. Check that exact program, including retained shared
                // classes; a previous tool check may have checked different or incomplete source.
                if (built.Files is not { Count: > 0 } || built.Files.ContainsKey(AppFiles.DeclarationPath)) { return reply; }
                var files = new Dictionary<string, string>(existingFiles, StringComparer.Ordinal);
                foreach (var (path, source) in built.Files) { files[path] = source; }
                var program = files.Where(file => AppFiles.IsProgram(file.Key))
                    .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
                if (program.Count == 0) { return reply; }
                var check = await Checked(program, cancellationToken);
                if (check.Success) { return reply; }
                if (progress is not null) { await progress.Progress("working", $"round {round + 1}: correcting final compile errors"); }
                messages.Add(new AiMessage("user", [new AiText("The exact final program does not compile. Fix these diagnostics and return the corrected implementation JSON:\n"
                    + JsonSerializer.Serialize(check, Json))]));
                continue;
            }
            if (progress is not null) { await progress.Progress(BuilderState(calls), $"round {round + 1}: {string.Join(", ", calls.Select(call => call.Name))}"); }
            var results = new List<AiContent>();
            foreach (var call in calls) { results.Add(new AiToolResult(call.CallId, await ExecuteBuilderTool(call, cancellationToken))); }
            messages.Add(new AiMessage("tool", results.ToArray()));
        }
        throw new InvalidDataException($"The Builder used {MaxBuilderRounds} rounds without producing a valid implementation.");
    }

    private static string BuilderState(AiToolCall[] calls)
        => calls.Any(call => call.Name == BuilderTools.SearchContracts) ? "searching"
         : calls.Any(call => call.Name is BuilderTools.ReadContracts or BuilderTools.ReadContractDetail) ? "reading"
         : "working";

    private async Task<string> ExecuteBuilderTool(AiToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            return call.Name switch
            {
                BuilderTools.SearchContracts => JsonSerializer.Serialize(
                    await BuilderTools.Search(search, Parse<BuilderTools.SearchArguments>(call.ArgumentsJson).Query, cancellationToken), Json),
                BuilderTools.ReadContracts => JsonSerializer.Serialize(
                    await Require(csharp).ReadContracts(Parse<BuilderTools.ReadArguments>(call.ArgumentsJson).Modules ?? [], cancellationToken), Json),
                BuilderTools.ReadContractDetail => await Detail(Parse<BuilderTools.DetailArguments>(call.ArgumentsJson)),
                BuilderTools.CheckCSharp => JsonSerializer.Serialize(
                    await Checked(Parse<BuilderTools.CheckArguments>(call.ArgumentsJson).Files ?? new Dictionary<string, string>(), cancellationToken), Json),
                _ => throw new ArgumentException($"Unknown tool '{call.Name}'."),
            };
        }
        // A failed tool call is the model's problem to correct, not the build's end.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { error = error.Message }, Json);
        }

        static T Require<T>(T? service) where T : class
            => service ?? throw new InvalidOperationException(AppAuthoringPolicy.SandboxMissing);

        async Task<string> Detail(BuilderTools.DetailArguments args) => JsonSerializer.Serialize(
            await Require(csharp).ReadContractDetail(args.ModuleId, args.ContractName, args.MethodName, cancellationToken), Json);
    }

    // check_csharp answers what the app gates will: the compiler check on the app bundle.
    private async Task<ScriptCompilationCheck> Checked(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        if (SynapseCheck.Compile(plain, files) is { } synapse) { return synapse; }
        return await (csharp ?? throw new InvalidOperationException(AppAuthoringPolicy.SandboxMissing)).Compile(files, cancellationToken);
    }

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
        AppOperation[]? Operations = null, AppSetting[]? Settings = null, AppSlot[]? Slots = null, UiChildRef? Entry = null);
    private sealed record BuiltApp(IReadOnlyDictionary<string, string>? Files,
        IReadOnlyList<BuiltSetting>? Settings = null, AppOperation[]? Operations = null, UiChildRef? Entry = null);
    private sealed record BuiltSetting(string Name, string? Description, string? Default);
}
