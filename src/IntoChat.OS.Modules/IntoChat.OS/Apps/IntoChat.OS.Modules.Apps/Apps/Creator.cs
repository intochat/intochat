using System.Text;
using System.Text.Json;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.Flutter;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Contracts;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Apps;

// The Creator's two agents on the app neuron. Author is one synchronous model call that
// shapes the spec; the Builder loop runs on the work queue, streaming progress into the
// neuron — never inside a request, never inside a grain turn.
internal sealed class Creator(
    IGrainFactory grains,
    NeuronTypeSearch search,
    IOptions<AppsOptions> options,
    AppAuthoringPolicy policy,
    AppWorkQueue queue,
    IScriptSandbox? csharp = null)
{
    private const int MaxAuthorRetries = 2;
    private const int MaxBuilderRounds = 12;
    private const int MaxRequestLength = 4000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string AuthorModel => options.Value.AuthorModel ?? nameof(IGpt56Luna);
    private string BuilderModel => options.Value.BuilderModel ?? nameof(IGpt56Luna);

    // With no name, the Author names the app and it exists — Drafted, in the caller's brain —
    // when this returns. With a name, it reshapes that app's spec.
    public async Task<AppSnapshot> Author(IIntoChatOS brain, string brainId, string? name, string request, CancellationToken cancellationToken)
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
                Icon: existing?.Manifest.Icon, Toolbar: existing?.Manifest.Toolbar);
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
            try { await reporting.Progress("attention", error.Message); } catch (Exception) { }
            throw;
        }
    }

    // Flips the app to Building and returns; the Builder loop lands the files in the background.
    public async Task<AppSnapshot> Implement(IIntoChatOS brain, string appKey, string? instruction)
    {
        ArgumentNullException.ThrowIfNull(brain);
        policy.RequireSandbox();
        var app = brain.Get<IApp>(appKey);
        var snapshot = await app.BeginBuild(instruction);
        var attempt = await app.CurrentBuildAttempt();
        queue.Enqueue($"implement {appKey}", (services, ct) => Build(appKey, attempt, snapshot, instruction, ct));
        return snapshot;
    }

    private async Task Build(string appKey, Guid attempt, AppSnapshot snapshot, string? instruction, CancellationToken cancellationToken)
    {
        // The queue's grain client is unstamped (trusted cluster path); progress and results
        // land on the neuron, which is the only durable record of this run.
        var app = grains.GetGrain<IApp>(appKey);
        try
        {
            var task = new StringBuilder()
                .AppendLine($"App: {snapshot.Manifest.Title}")
                .AppendLine().AppendLine("Specification:").AppendLine(snapshot.Spec);
            task.AppendLine().AppendLine("Manifest:").AppendLine(JsonSerializer.Serialize(snapshot.Manifest, Json));
            if (!string.IsNullOrWhiteSpace(instruction)) { task.AppendLine().AppendLine("The person asked:").AppendLine(instruction.Trim()); }
            if (snapshot.Files.Count > 0)
            {
                task.AppendLine().AppendLine("Current files:");
                foreach (var (path, source) in snapshot.Files) { task.AppendLine($"--- {path} ---").AppendLine(source); }
            }
            await app.Progress("working", $"Building {snapshot.Manifest.Title}");
            var built = Parse<BuiltApp>(await BuilderConversation(BrainKey.BrainOf(appKey), app, task.ToString(), snapshot.Files, cancellationToken));
            var files = new Dictionary<string, string>(built.Files ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            if (files.Count == 0)
            {
                await app.CompleteBuild(attempt, null, "the Builder produced no files");
                await ReportIfCurrent(app, attempt, "attention", "The Builder produced no files");
                return;
            }
            // Manifest changes land with the files, inside the attempt check: a stale
            // worker's manifest is discarded with its files.
            var manifest = snapshot.Manifest with
            {
                Operations = built.Operations is { Length: > 0 } ? built.Operations : snapshot.Manifest.Operations,
                Settings = built.Settings is { Count: > 0 }
                        ? [.. built.Settings.Select(setting => new AppSetting(setting.Name, setting.Description ?? "", setting.Default ?? ""))]
                        : snapshot.Manifest.Settings,
            };
            var completed = await app.CompleteBuild(attempt, files, null, manifest);
            if (completed.Status == AppStatus.Live) { await ReportIfCurrent(app, attempt, "completed", $"Built {snapshot.Manifest.Title}"); }
            else { await ReportIfCurrent(app, attempt, "attention", completed.Activity.LastOrDefault() ?? "The build did not complete"); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await app.CompleteBuild(attempt, null, error.Message);
            // Reporting must not mask the build's own error.
            try { await ReportIfCurrent(app, attempt, "attention", error.Message); } catch (Exception) { }
        }
    }

    // A stale worker's outcome was discarded by the neuron; it must not show as the app's activity either.
    private static async Task ReportIfCurrent(IApp app, Guid attempt, string state, string detail)
    {
        if (await app.CurrentBuildAttempt() == attempt) { await app.Progress(state, detail); }
    }

    private async Task<AuthoredApp> Authored(string brainId, string prompt, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Parse<AuthoredApp>(await grains.GetGrain<ICreatorModel>(0).Complete(brainId, AuthorModel, AgentPrompts.Author, prompt));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException && retry < MaxAuthorRetries)
            {
                prompt += $"\n\nYour previous reply could not be used: {error.Message} Reply with the JSON object only.";
            }
        }
    }

    // The Builder works with tools: it searches the contract catalog, reads details and
    // compile-checks its C# before answering. Each round lands on the app's activity feed.
    private async Task<string> BuilderConversation(string brainId, IApp app, string task, IReadOnlyDictionary<string, string> existingFiles, CancellationToken cancellationToken)
    {
        var messages = new List<AiMessage>
        {
            new("system", [new AiText(AgentPrompts.Builder + (csharp is null ? "" : "\n\n" + csharp.AuthoringDescription))]),
            new("user", [new AiText(task)]),
        };
        for (var round = 0; round < MaxBuilderRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await grains.GetGrain<ICreatorModel>(0).Generate(brainId, BuilderModel, new InferenceRequest(messages, Tools: BuilderTools.Definitions));
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
                await app.Progress("working", $"round {round + 1}: correcting final compile errors");
                messages.Add(new AiMessage("user", [new AiText("The exact final program does not compile. Fix these diagnostics and return the corrected implementation JSON:\n"
                    + JsonSerializer.Serialize(check, Json))]));
                continue;
            }
            await app.Progress(BuilderState(calls), $"round {round + 1}: {string.Join(", ", calls.Select(call => call.Name))}");
            var results = new List<AiContent>();
            foreach (var call in calls) { results.Add(new AiToolResult(call.CallId, await ExecuteBuilderTool(call, cancellationToken))); }
            messages.Add(new AiMessage("tool", results.ToArray()));
        }
        throw new InvalidDataException($"The Builder used {MaxBuilderRounds} rounds without producing a valid implementation.");
    }

    private static string BuilderState(AiToolCall[] calls)
        => calls.Any(call => call.Name == BuilderTools.SearchContracts) ? "searching"
         : calls.Any(call => call.Name == BuilderTools.ReadContracts) ? "reading"
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
    }

    // check_csharp answers what the app gates will: the compile errors plus every synapse that is not one reaction.
    private async Task<ScriptCompilationCheck> Checked(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        var compiled = await (csharp ?? throw new InvalidOperationException(AppAuthoringPolicy.SandboxMissing)).Compile(files, cancellationToken);
        var shape = SynapseShape.Check(files);
        return shape.Length == 0 ? compiled : compiled with { Success = false, Errors = [.. compiled.Errors, .. shape] };
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

    private sealed record AuthoredApp(string Name, string Title, string Description, string Spec,
        AppOperation[]? Operations = null, AppSetting[]? Settings = null, AppSlot[]? Slots = null, UiChildRef? Entry = null);
    private sealed record BuiltApp(IReadOnlyDictionary<string, string>? Files,
        IReadOnlyList<BuiltSetting>? Settings = null, AppOperation[]? Operations = null, UiChildRef? Entry = null);
    private sealed record BuiltSetting(string Name, string? Description, string? Default);
}
