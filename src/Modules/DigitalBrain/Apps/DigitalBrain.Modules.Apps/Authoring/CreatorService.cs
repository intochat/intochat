using System.Text;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.OpenAI;
using DigitalBrain.Framework;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Apps;

// The Creator's agents operate on the live app: Author shapes the scenario document,
// Builder writes the behaviors. The loops run here, in a service — never in a grain turn —
// so the app stays responsive while a conversation is in flight.
internal sealed class CreatorService(
    IGrainFactory grains,
    ContractCatalog catalog,
    NeuronTypeSearch search,
    IOptions<AppsOptions> options,
    IEnumerable<IAppRuntime> runtimes,
    AppAuthoringPolicy policy,
    IScriptSandbox? csharp = null)
{
    private const int MaxAuthorRetries = 2;
    private const int MaxBuilderRounds = 12;
    private const int MaxRequestLength = 4000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string AuthorModel => options.Value.AuthorModel ?? nameof(IGpt56Luna);
    private string BuilderModel => options.Value.BuilderModel ?? nameof(IGpt56Luna);
    private string RuntimeDescriptions => string.Join("\n\n", runtimes.Select(runtime => runtime.AuthoringDescription)
        .Concat(csharp?.CanRun == true ? [csharp.AuthoringDescription] : []));

    // Starts a brand-new app from a request: the Author names it, and it exists — installed,
    // running, in the caller's brain — before the person has seen a line of it.
    public async Task<(PackageId Package, AppContent Content)> Start(IDigitalBrain brain, string request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var owner = DigitalBrain.Framework.Enforcement.CallerContextStamper.Require().PrincipalId;
        var named = await Authored($"Write the specification for this app.\n\nWhat the person wants:\n{request?.Trim()}", cancellationToken);
        var package = PackageId.Create(owner, named.Name);
        var app = brain.Get<IApp>(DigitalBrain.Framework.Enforcement.BrainScope.CurrentId() + "/packages/" + package);
        await Apply(app, named, await app.Read());
        return (package, await app.ReadContent());
    }

    // Shapes (or reshapes) the app from a request: creates it if it does not exist yet, then
    // makes the scenario document canonical and the spec a derived file.
    public async Task<AppContent> Author(IApp app, string request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (request.Length > MaxRequestLength) { throw new ArgumentException($"Describe the app in at most {MaxRequestLength} characters."); }
        var existing = await app.Read();
        var prompt = existing.Status == AppStatus.Installed
            ? $"Revise this app.\n\nCurrent specification:\n{Spec(await app.ReadContent())}\n\nTheir change request:\n{request.Trim()}"
            : $"Write the specification for this app.\n\nWhat the person wants:\n{request.Trim()}";
        var authored = await Authored(prompt, cancellationToken);
        await Apply(app, authored, existing);
        return await app.ReadContent();
    }

    private async Task<AuthoredApp> Authored(string prompt, CancellationToken cancellationToken)
    {
        prompt += $"\n\nRuntimes this brain can run: {string.Join(", ", policy.AvailableRuntimes)}.";
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var authored = Parse<AuthoredApp>(await grains.GetGrain<ICreatorModel>(0).Complete(AuthorModel, AgentPrompts.Author + "\n\n" + RuntimeDescriptions, prompt));
                if (!policy.AvailableRuntimes.Contains(authored.Runtime)) { throw new InvalidDataException($"'{authored.Runtime}' is not one of the runtimes {string.Join(", ", policy.AvailableRuntimes)}."); }
                if (authored.Document is not null) { AppDocumentCodec.Validate(authored.Document); }
                return authored;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException && retry < MaxAuthorRetries)
            {
                prompt += $"\n\nYour previous reply could not be used: {error.Message} Reply with the JSON object only.";
            }
        }
    }

    private static async Task Apply(IApp app, AuthoredApp authored, AppSnapshot existing)
    {
        if (existing.Status != AppStatus.Installed)
        { await app.Create(new(Guid.NewGuid(), authored.Title, authored.Description, authored.Runtime)); }
        else
        {
            var manifest = (await app.ReadContent()).Manifest;
            await app.SaveManifest(new(authored.Title, authored.Description, [.. manifest.Operations], [.. manifest.Settings], Runtime: authored.Runtime), Guid.NewGuid());
        }
        if (authored.Document is not null) { await app.SaveDocument(authored.Document, Guid.NewGuid()); }
        await app.WriteFile(PackageContent.SpecPath,
            authored.Document is null ? authored.Spec : AppDocumentCodec.ExportSpec(authored.Document), Guid.NewGuid());
    }

    // Implements the app's document: the Builder writes tests.cs and the behaviors, each
    // behavior landing on the live app as a hot-swap.
    public async Task<AppContent> Implement(IApp app, string? instruction, CancellationToken cancellationToken)
    {
        var content = await app.ReadContent();
        policy.RequireSandbox();
        policy.RequireRuntime(content.Manifest.RuntimeName);
        var task = new StringBuilder()
            .AppendLine($"Runtime: {content.Manifest.RuntimeName}")
            .AppendLine($"App: {content.Manifest.Title}")
            .AppendLine().AppendLine("Specification:").AppendLine(Spec(content));
        if (!string.IsNullOrWhiteSpace(instruction)) { task.AppendLine().AppendLine("The person asked:").AppendLine(instruction.Trim()); }
        if (content.Document is not null)
        { task.AppendLine("Authoring document (preserve IDs and prose):").AppendLine(AppDocumentCodec.Encode(content.Document)); }
        var built = Parse<BuiltApp>(await BuilderConversation(task.ToString(), cancellationToken));
        var files = new Dictionary<string, string>(built.Files ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(built.Source)) { files[PackageContent.SourcePath] = built.Source; }
        if (!files.ContainsKey(PackageContent.TestsPath) && !content.Files.ContainsKey(PackageContent.TestsPath))
        { throw new InvalidDataException($"The implementation must include {PackageContent.TestsPath}, the scenarios' proof."); }
        var document = content.Document;
        if (document is not null)
        {
            var bindings = built.BehaviorSources ?? [];
            if (bindings.Select(binding => binding.BehaviorId).Distinct(StringComparer.Ordinal).Count() != bindings.Length
                || bindings.Any(binding => !document.Behaviors.Any(declared => declared.Id == binding.BehaviorId)))
            { throw new InvalidDataException("Builder source bindings must refer to unique existing behavior IDs."); }
            document = document with
            {
                Behaviors = document.Behaviors.Select(behavior => behavior with
                { SourcePaths = bindings.FirstOrDefault(binding => binding.BehaviorId == behavior.Id)?.SourcePaths ?? behavior.SourcePaths }).ToArray(),
            };
        }
        if (built.Operations is { Length: > 0 } || built.Settings is { Count: > 0 })
        {
            await app.SaveManifest(new(content.Manifest.Title, content.Manifest.Description,
                built.Operations ?? [.. content.Manifest.Operations],
                [.. (built.Settings ?? []).Select(setting => new PackageSetting(setting.Name, setting.Description ?? "", setting.Default ?? ""))],
                Runtime: content.Manifest.RuntimeName), Guid.NewGuid());
        }
        foreach (var (path, source) in files)
        {
            if (path == PackageContent.SourcePath
                || (path.StartsWith(PackageContent.BehaviorsPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal)))
            { await app.WriteBehavior(path, source, Guid.NewGuid()); }
            else { await app.WriteFile(path, source, Guid.NewGuid()); }
        }
        if (document is not null) { await app.SaveDocument(document, Guid.NewGuid()); }
        return await app.ReadContent();
    }

    private static string Spec(AppContent content)
        => content.Document is { } document ? AppDocumentCodec.ExportSpec(document) : content.Files.GetValueOrDefault(PackageContent.SpecPath, "");

    // The Builder works with tools: it searches the contract catalog, reads details and
    // compile-checks its C# before answering.
    private async Task<string> BuilderConversation(string task, CancellationToken cancellationToken)
    {
        var messages = new List<AiMessage>
        {
            new("system", [new AiText(AgentPrompts.Builder + "\n\n" + RuntimeDescriptions)]),
            new("user", [new AiText(task)]),
        };
        for (var round = 0; round < MaxBuilderRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await grains.GetGrain<ICreatorModel>(0).Generate(BuilderModel, new InferenceRequest(messages, Tools: BuilderTools.Definitions));
            messages.AddRange(result.Messages);
            var calls = result.Messages.SelectMany(message => message.Content).OfType<AiToolCall>().ToArray();
            if (calls.Length == 0)
            {
                return string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
            }
            var results = new List<AiContent>();
            foreach (var call in calls) { results.Add(new AiToolResult(call.CallId, await ExecuteBuilderTool(call, cancellationToken))); }
            messages.Add(new AiMessage("tool", results));
        }
        throw new InvalidDataException($"The Builder used tools for {MaxBuilderRounds} rounds without producing the implementation.");
    }

    private async Task<string> ExecuteBuilderTool(AiToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            return call.Name switch
            {
                BuilderTools.SearchContracts => JsonSerializer.Serialize(
                    await BuilderTools.Search(catalog, search, Parse<BuilderTools.SearchArguments>(call.ArgumentsJson).Query), Json),
                BuilderTools.ReadContracts => JsonSerializer.Serialize(
                    await Require(csharp).ReadContracts(Parse<BuilderTools.ReadArguments>(call.ArgumentsJson).Modules ?? [], cancellationToken), Json),
                BuilderTools.CheckCSharp => JsonSerializer.Serialize(
                    Require(csharp).Check(Parse<BuilderTools.CheckArguments>(call.ArgumentsJson).Files ?? new Dictionary<string, string>()), Json),
                _ => throw new ArgumentException($"Unknown tool '{call.Name}'."),
            };
        }
        // A failed tool call is the model's problem to correct, not the edit's end.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { error = error.Message }, Json);
        }

        static T Require<T>(T? service) where T : class
            => service ?? throw new InvalidOperationException(AppAuthoringPolicy.SandboxMissing);
    }

    // Models sometimes wrap the JSON in a code fence or add prose around it; read the first complete object.
    private static T Parse<T>(string reply)
    {
        var start = reply.IndexOf('{', StringComparison.Ordinal);
        if (start < 0) { throw new InvalidDataException("The reply contains no JSON object."); }
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(reply[start..]));
        using var document = JsonDocument.ParseValue(ref reader);
        return document.Deserialize<T>(Json) ?? throw new InvalidDataException("The reply's JSON object is empty.");
    }

    private sealed record AuthoredApp(string Name, string Title, string Description, string Runtime, string Spec, AppAuthoringDocument? Document = null);
    private sealed record BuiltApp(IReadOnlyList<BuiltSetting>? Settings, IReadOnlyDictionary<string, string>? Files, string? Source, BehaviorSourceBinding[]? BehaviorSources = null, PackageOperation[]? Operations = null);
    private sealed record BehaviorSourceBinding(string BehaviorId, string[] SourcePaths);
    private sealed record BuiltSetting(string Name, string? Description, string? Default);
}
