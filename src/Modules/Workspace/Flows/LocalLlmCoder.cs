using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Options controlling <see cref="LocalLlmCoder"/> model selection, tool round budget, timeout, and frontier escalation.
/// </summary>
internal sealed class LocalLlmCoderOptions
{
    public string? PreferredModel { get; set; }
    public int MaxToolRounds { get; set; } = 12;
    public TimeSpan? Timeout { get; set; }
    public int? ContextWindowTokens { get; set; } = 16384;
    public bool EscalateToFrontierOnRepeatedFailure { get; set; } = true;
}

/// <summary>
/// Local-first <see cref="ICoder"/> and shared tool-calling loop used by both <see cref="LocalLlmCoder"/>
/// and <see cref="InoNativeHarness"/> slice workers. Confines all file and Roslyn operations inside the worktree
/// via <see cref="ConfinedWorkTreeSandbox"/> and <see cref="ChangePolicy"/>.
/// </summary>
internal sealed class LocalLlmCoder : ICoder
{
    private const int MaxListedFiles = 200;
    private const int MaxReadCharacters = 24_000;

    internal sealed record ModelTurnRunner(
        IModelInference Inference,
        ModelSelection? Selection,
        string ModelId,
        bool SupportsTemperature,
        int? OllamaContextWindowTokens)
    {
        public Task<InferenceResult> InvokeAsync(
            IReadOnlyList<AiMessage> messages,
            IReadOnlyList<InferenceTool> tools,
            string? brainId,
            CancellationToken ct)
        {
            var temperature = SupportsTemperature ? 0.1f : (float?)null;
            var provider = OllamaContextWindowTokens is { } ctx ? new OllamaInferenceOptions(ContextWindowTokens: ctx) : null;
            var options = temperature is not null || provider is not null ? new InferenceOptions(Temperature: temperature, Provider: provider) : null;
            return Inference.Generate(new InferenceRequest(messages, Model: Selection, Options: options, Tools: tools), model: null, cancellationToken: ct, brainId: brainId);
        }
    }

    internal sealed record ToolLoopRequest(
        string WorktreePath,
        string SystemPrompt,
        string UserPrompt,
        ChangePolicy Policy,
        IWorktreeRoslyn Roslyn,
        int MaxToolRounds,
        string DefaultSummary,
        string NudgePrompt,
        string ExhaustionMessagePrefix,
        BrainId? Brain = null,
        string[]? SliceOwnedPaths = null,
        InoCodeOrchestrator? BatchOrchestrator = null,
        ModelTurnRunner? ExplicitRunner = null,
        LocalLlmCoderOptions? Options = null,
        bool RejectTierDowngrade = false);

    private readonly IServiceProvider? _services;
    private readonly LocalLlmCoderOptions _options;
    private readonly ILogger<LocalLlmCoder> _logger;
    private readonly ChangePolicy _policy;
    private readonly IWorktreeRoslyn _roslyn;

    public LocalLlmCoder(
        IServiceProvider? services = null,
        IConfiguration? configuration = null,
        LocalLlmCoderOptions? options = null,
        ILogger<LocalLlmCoder>? logger = null,
        ChangePolicy? policy = null,
        IWorktreeRoslyn? roslyn = null)
    {
        _services = services;
        _logger = logger ?? OsConsoleLog.For<LocalLlmCoder>();
        _options = options ?? BindOptions(configuration);
        _policy = policy ?? services?.GetService<ChangePolicy>() ?? ChangePolicy.Default;
        _roslyn = roslyn ?? services?.GetService<IWorktreeRoslyn>() ?? UnavailableWorktreeRoslyn.Instance;
    }

    public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var sandbox = new ConfinedWorkTreeSandbox(lease.TreePath, _policy, _roslyn);
        var initialListing = sandbox.ListFiles(null, null, maxItems: 60);
        var userPrompt = $"Repository: {lease.RepositoryId} (base branch: {lease.BaseBranch})\nGoal: {goal}\n\nTop-level / initial files in worktree:\n{initialListing}\n\nUse the file tools to inspect and apply the requested change now.";
        var loopRequest = new ToolLoopRequest(
            WorktreePath: lease.TreePath,
            SystemPrompt: BuildSystemPrompt(_policy),
            UserPrompt: userPrompt,
            Policy: _policy,
            Roslyn: _roslyn,
            MaxToolRounds: _options.MaxToolRounds,
            DefaultSummary: "Applied requested code changes.",
            NudgePrompt: "No files have been modified yet. Call replace_in_file or write_file to make the requested change on disk, then summarize.",
            ExhaustionMessagePrefix: "Coder exhausted",
            Options: _options,
            RejectTierDowngrade: true);

        string? attemptedModelId = null;
        ExceptionDispatchInfo failure;
        try
        {
            return await RunWithTimeoutAsync(sandbox, loopRequest, runner: null, m => attemptedModelId = m, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Local LLM coder model '{Model}' failed the coding task.", attemptedModelId ?? "unresolved");
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        if (failure.SourceException is not SecurityException
            && _options.EscalateToFrontierOnRepeatedFailure
            && await ResolveFrontierRunnerAsync(_services, brain: null, _logger, ct).ConfigureAwait(false) is { } frontier
            && !string.Equals(frontier.ModelId, attemptedModelId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("frontier-escalation: escalating from local coder model '{LocalModel}' to frontier model '{FrontierModel}'.", attemptedModelId ?? "unresolved", frontier.ModelId);
            try
            {
                return await RunWithTimeoutAsync(sandbox, loopRequest, frontier, m => attemptedModelId = m, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "frontier-escalation: frontier model '{FrontierModel}' also failed the coding task.", frontier.ModelId);
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure.Throw();
        throw new UnreachableException();
    }

    private async Task<CoderEditResult> RunWithTimeoutAsync(
        ConfinedWorkTreeSandbox sandbox,
        ToolLoopRequest request,
        ModelTurnRunner? runner,
        Action<string> onModelResolved,
        CancellationToken ct)
    {
        var timeout = _options.Timeout ?? TimeSpan.FromMinutes(4);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await RunToolLoopAsync(_services, request with { ExplicitRunner = runner }, sandbox, onModelResolved, _logger, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Local LLM coder timed out after {timeout}.");
        }
    }

    internal static async Task<CoderEditResult> RunToolLoopAsync(
        IServiceProvider? services,
        ToolLoopRequest request,
        ConfinedWorkTreeSandbox? existingSandbox = null,
        Action<string>? onModelResolved = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var log = logger ?? OsConsoleLog.For<LocalLlmCoder>();
        var runner = request.ExplicitRunner
            ?? await ResolveModelRunnerAsync(services, request.Brain, request.Options, request.RejectTierDowngrade, log, ct).ConfigureAwait(false);
        onModelResolved?.Invoke(runner.ModelId);

        var sandbox = existingSandbox ?? new ConfinedWorkTreeSandbox(
            request.WorktreePath,
            request.Policy,
            request.Roslyn,
            request.SliceOwnedPaths,
            request.BatchOrchestrator);
        var tools = CreateToolDefinitions(request.Policy, includeBatchEdit: request.BatchOrchestrator is not null);
        var messages = new List<AiMessage>
        {
            new("system", [new AiText(request.SystemPrompt)]),
            new("user", [new AiText(request.UserPrompt)]),
        };

        var lastSummary = request.DefaultSummary;
        for (var round = 0; round < request.MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await runner.InvokeAsync(messages, tools, request.Brain?.Value, ct).ConfigureAwait(false);
            var assistant = reply.Messages.LastOrDefault(static m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                ?? reply.Messages.LastOrDefault()
                ?? new AiMessage("assistant", []);
            messages.Add(assistant);

            var toolCalls = assistant.Content.OfType<AiToolCall>().ToArray();
            var textSummary = string.Join("\n", assistant.Content.OfType<AiText>().Select(static t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(textSummary)) { lastSummary = textSummary; }

            if (toolCalls.Length == 0)
            {
                if (sandbox.TouchedFiles.Count > 0)
                {
                    ValidateTouchedFiles(sandbox.TouchedFiles, request.Policy, request.SliceOwnedPaths);
                    return new CoderEditResult(lastSummary, sandbox.TouchedFiles, runner.ModelId);
                }

                messages.Add(new AiMessage("user", [new AiText(request.NudgePrompt)]));
                continue;
            }

            var toolResults = new List<AiContent>(toolCalls.Length);
            foreach (var call in toolCalls)
            {
                var output = await sandbox.ExecuteToolAsync(call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
                toolResults.Add(new AiToolResult(call.CallId, EnsureJsonToolResult(output)));
            }

            messages.Add(new AiMessage("tool", [.. toolResults]));
        }

        if (sandbox.TouchedFiles.Count > 0)
        {
            ValidateTouchedFiles(sandbox.TouchedFiles, request.Policy, request.SliceOwnedPaths);
            return new CoderEditResult(lastSummary, sandbox.TouchedFiles, runner.ModelId);
        }

        throw new InvalidOperationException($"{request.ExhaustionMessagePrefix} {request.MaxToolRounds} tool rounds using '{runner.ModelId}' without modifying any file.");
    }

    private static void ValidateTouchedFiles(IReadOnlyList<string> touchedFiles, ChangePolicy policy, string[]? sliceOwnedPaths)
    {
        if (sliceOwnedPaths is { Length: > 0 })
        {
            foreach (var touched in touchedFiles)
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(touched, sliceOwnedPaths);
            }
        }
        else if (policy.Check([.. touchedFiles.Select(RepoPath.Parse)]) is { } refusal)
        {
            throw new SecurityException(refusal);
        }
    }

    internal static async Task<ModelTurnRunner> ResolveModelRunnerAsync(
        IServiceProvider? services,
        BrainId? brain,
        LocalLlmCoderOptions? options,
        bool rejectTierDowngrade,
        ILogger logger,
        CancellationToken ct)
    {
        var catalog = services?.GetService<ModelCatalog>();
        var inference = services?.GetService<IModelInference>();
        var explicitSelection = string.IsNullOrWhiteSpace(options?.PreferredModel) ? null : new ModelSelection(options.PreferredModel.Trim());

        if (catalog is not null)
        {
            var resolved = await catalog.Decide(brain?.Value, ModelRole.Think, explicitSelection, requiresTools: true, ct).ConfigureAwait(false);
            if (resolved.Provider == AiProvider.Host && services?.GetService<IChatClient>() is null)
            {
                throw new NoProviderConfiguredException("No configured tool-capable AI model or local Ollama server is available for coding.");
            }

            if (rejectTierDowngrade
                && explicitSelection is null
                && resolved.Profile is null
                && KnownModels.Find(resolved.Model) is { Tier: not Tier.Deep } downgraded)
            {
                var expectedThink = KnownModels.Defaults.Device.TryGetValue(ModelRole.Think, out var thinkId) ? thinkId.Value : KnownModels.Qwen25Coder14B.Id.Value;
                throw new NoProviderConfiguredException(
                    $"No Think/Deep model (such as '{expectedThink}') is available for coding; refusing to silently downgrade to '{downgraded.Id.Value}' ({downgraded.Tier} tier). Pull '{expectedThink}' in Ollama, configure a Think model, or set IntoChatOS:Coder:Model explicitly.");
            }

            var effectiveInference = inference ?? throw new InvalidOperationException("IModelInference must be registered alongside ModelCatalog.");
            var selection = ToSelection(resolved);
            var desc = await effectiveInference.Describe(selection, model: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
            var ollamaCtx = ResolveOllamaContextWindow(resolved.Provider == AiProvider.Ollama, desc, options);
            return new ModelTurnRunner(effectiveInference, selection, resolved.Model, desc.Temperature == CapabilitySupport.Supported, ollamaCtx);
        }

        if (inference is not null)
        {
            List<ModelSelection?> candidates = explicitSelection is not null ? [explicitSelection, null] : [null];
            foreach (var selection in candidates)
            {
                try
                {
                    var desc = await inference.Describe(selection, model: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
                    if (desc.Tools == CapabilitySupport.Supported
                        && (!string.Equals(desc.Provider, "Host", StringComparison.OrdinalIgnoreCase) || services?.GetService<IChatClient>() is not null))
                    {
                        var isOllama = string.Equals(desc.Provider, nameof(AiProvider.Ollama), StringComparison.OrdinalIgnoreCase);
                        return new ModelTurnRunner(inference, selection, desc.Model, desc.Temperature == CapabilitySupport.Supported, ResolveOllamaContextWindow(isOllama, desc, options));
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is ArgumentException or InvalidOperationException or NotSupportedException or ProviderUnavailableException)
                {
                    logger.LogWarning(ex, "Configured IModelInference candidate '{Model}' could not resolve.", selection?.Model ?? "default");
                }
            }
        }

        throw new InvalidOperationException("No configured tool-capable AI model or local Ollama server is available for coding.");
    }

    internal static async Task<ModelTurnRunner?> ResolveFrontierRunnerAsync(
        IServiceProvider? services,
        BrainId? brain,
        ILogger logger,
        CancellationToken ct)
    {
        if (services?.GetService<ModelCatalog>() is not { } catalog || services.GetService<IModelInference>() is not { } inference)
        {
            return null;
        }

        try
        {
            if (await catalog.DecideFrontier(brain, ModelRole.Think, requiresTools: true, ct).ConfigureAwait(false) is not { } frontier)
            {
                return null;
            }

            var selection = ToSelection(frontier);
            var desc = await inference.Describe(selection, model: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
            return new ModelTurnRunner(inference, selection, frontier.Model, desc.Temperature == CapabilitySupport.Supported, OllamaContextWindowTokens: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogSwallowed(ex, "frontier model resolution failed; keeping the local outcome");
            return null;
        }
    }

    private static int? ResolveOllamaContextWindow(bool isOllama, ModelDescriptor desc, LocalLlmCoderOptions? options)
        => isOllama && options?.ContextWindowTokens is { } req and > 0 && desc.ContextWindowTokens is { } max and > 0
            ? (int)Math.Min(req, max)
            : null;

    internal static ModelSelection? ToSelection(ResolvedModel resolved)
        => resolved.Provider == AiProvider.Host ? null
            : resolved.Profile is { Length: > 0 } ? new($"profile:{resolved.Profile}", resolved.Reasoning, resolved.MaxOutputTokens)
            : resolved.Provider == AiProvider.Ollama ? new($"ollama/{resolved.Model}", resolved.Reasoning, resolved.MaxOutputTokens)
            : new(resolved.Model, resolved.Reasoning, resolved.MaxOutputTokens);

    internal static LocalLlmCoderOptions BindOptions(IConfiguration? configuration)
    {
        var opts = new LocalLlmCoderOptions();
        if (configuration is null) { return opts; }

        opts.PreferredModel = configuration["IntoChatOS:Coder:Model"];
        if (int.TryParse(configuration["IntoChatOS:Coder:MaxToolRounds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRounds) && maxRounds > 0) { opts.MaxToolRounds = maxRounds; }
        if (double.TryParse(configuration["IntoChatOS:Coder:TimeoutSeconds"], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0) { opts.Timeout = TimeSpan.FromSeconds(seconds); }
        if (int.TryParse(configuration["IntoChatOS:Coder:ContextWindowTokens"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var contextWindow) && contextWindow > 0) { opts.ContextWindowTokens = contextWindow; }
        if (bool.TryParse(configuration["IntoChatOS:Coder:EscalateToFrontier"], out var escalate)) { opts.EscalateToFrontierOnRepeatedFailure = escalate; }
        return opts;
    }

    internal static string EnsureJsonToolResult(string? output)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            try { using var _ = JsonDocument.Parse(output); return output; }
            catch (JsonException) { return JsonSerializer.Serialize(new ToolContentPayload(true, output), CatalogAotJsonContext.Relaxed.ToolContentPayload); }
        }

        return JsonSerializer.Serialize(new ToolContentPayload(true, output ?? string.Empty), CatalogAotJsonContext.Relaxed.ToolContentPayload);
    }

    private static IReadOnlyList<InferenceTool> CreateToolDefinitions(ChangePolicy policy, bool includeBatchEdit)
    {
        var extensions = string.Join(", ", policy.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase));
        var tools = new List<InferenceTool>(includeBatchEdit ? 8 : 7)
        {
            new("list_files", "List allowed repository files under a relative directory (or optional substring filter). Never accesses outside the worktree.",
                """{"type":"object","properties":{"directory":{"type":"string","description":"Optional relative directory path such as src/Modules"},"filter":{"type":"string","description":"Optional filename or path substring filter"}}}"""),
            new("read_file", "Read the UTF-8 contents of a single file inside the worktree by repo-relative path.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path, e.g. README.md or src/Foo.cs"}},"required":["path"]}"""),
            new("replace_in_file", "Replace an exact target snippet with replacement text in an existing file inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path"},"old_text":{"type":"string","description":"Exact existing text to replace"},"new_text":{"type":"string","description":"New text"}},"required":["path","old_text","new_text"]}"""),
            new("write_file", $"Create or overwrite an allowed file ({extensions}) inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path"},"content":{"type":"string","description":"Full UTF-8 file content"}},"required":["path","content"]}"""),
            new("roslyn_symbols", "Inspect C# types, namespaces, and members in the worktree via Roslyn syntax analysis.",
                """{"type":"object","properties":{"filter":{"type":"string","description":"Optional relative path or filename substring filter"}}}"""),
            new("roslyn_diagnostics", "Run Roslyn C# syntax and semantic diagnostics on worktree C# files.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Optional repo-relative .cs file path to analyze"}}}"""),
            new("roslyn_edit_member", "Apply an AST-validated, Roslyn-formatted C# edit (add_using, add_or_replace_member, remove_member, create_type_file) inside the worktree.",
                """{"type":"object","properties":{"kind":{"type":"string","description":"add_using | add_or_replace_member | remove_member | create_type_file"},"path":{"type":"string","description":"Repo-relative .cs path"},"type_name":{"type":"string"},"member_name":{"type":"string"},"namespace_name":{"type":"string"},"source":{"type":"string","description":"Complete C# member declaration or type body"},"type_kind":{"type":"string"},"base_types":{"type":"string"}},"required":["kind","path"]}"""),
        };

        if (includeBatchEdit)
        {
            tools.Add(new("roslyn_batch_edit", "Execute a deterministic multi-edit C# AST batch via InoCodeOrchestrator in a single turn.",
                """{"type":"object","properties":{"edits":{"type":"array","items":{"type":"object","properties":{"kind":{"type":"string"},"path":{"type":"string"},"type_name":{"type":"string"},"member_name":{"type":"string"},"namespace_name":{"type":"string"},"source":{"type":"string"},"type_kind":{"type":"string"},"base_types":{"type":"string"}},"required":["kind","path"]}}},"required":["edits"]}"""));
        }

        return tools;
    }

    private static string BuildSystemPrompt(ChangePolicy policy) => $"""
        You are Ino's surgical coding engine operating inside an isolated Git worktree.
        Rules:
        {policy.Describe()}
        3. Always inspect existing files with list_files / read_file / roslyn_symbols before editing, unless creating a brand-new file explicitly requested by the goal.
        4. Prefer roslyn_edit_member + roslyn_diagnostics for C# AST-validated edits, and replace_in_file or write_file for Dart (.dart), Markdown (.md), or small file edits.
        5. Once you have made at least one real file modification that satisfies the goal, reply with a concise 1-2 sentence summary of the change and stop calling tools.
        """;

    internal sealed class ConfinedWorkTreeSandbox
    {
        private readonly string _root;
        private readonly ChangePolicy _policy;
        private readonly IWorktreeRoslyn _roslyn;
        private readonly string[]? _sliceOwnedPaths;
        private readonly InoCodeOrchestrator? _batchOrchestrator;
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

        public ConfinedWorkTreeSandbox(
            string treeRoot,
            ChangePolicy? policy = null,
            IWorktreeRoslyn? roslyn = null,
            string[]? sliceOwnedPaths = null,
            InoCodeOrchestrator? batchOrchestrator = null)
        {
            _root = Path.GetFullPath(treeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _policy = policy ?? ChangePolicy.Default;
            _roslyn = roslyn ?? UnavailableWorktreeRoslyn.Instance;
            _sliceOwnedPaths = sliceOwnedPaths;
            _batchOrchestrator = batchOrchestrator;
        }

        public IReadOnlyList<string> TouchedFiles => [.. _touched.OrderBy(p => p, StringComparer.Ordinal)];

        public async Task<string> ExecuteToolAsync(string name, string argumentsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                var args = doc.RootElement;

                if (_sliceOwnedPaths is { Length: > 0 } && name is "write_file" or "replace_in_file" or "roslyn_edit_member"
                    && args.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                {
                    InoCodeOrchestrator.EnsureWithinSliceOwnership(pathEl.GetString() ?? string.Empty, _sliceOwnedPaths);
                }

                return name switch
                {
                    "list_files" => ListFiles(GetOptionalString(args, "directory"), GetOptionalString(args, "filter"), MaxListedFiles),
                    "read_file" => await ReadFileAsync(RequireString(args, "path"), ct).ConfigureAwait(false),
                    "replace_in_file" => await ReplaceInFileAsync(RequireString(args, "path"), RequireString(args, "old_text"), RequireString(args, "new_text"), ct).ConfigureAwait(false),
                    "write_file" => await WriteFileAsync(RequireString(args, "path"), RequireString(args, "content"), ct).ConfigureAwait(false),
                    "roslyn_symbols" => await InspectRoslynSymbolsAsync(GetOptionalString(args, "filter"), ct).ConfigureAwait(false),
                    "roslyn_diagnostics" => await InspectRoslynDiagnosticsAsync(GetOptionalString(args, "path"), ct).ConfigureAwait(false),
                    "roslyn_edit_member" => await ApplyRoslynEditAsync(args, ct).ConfigureAwait(false),
                    "roslyn_batch_edit" when _batchOrchestrator is not null => await ApplyRoslynBatchEditAsync(args, ct).ConfigureAwait(false),
                    _ => JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, $"Unknown tool '{name}'."), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload),
                };
            }
            catch (SecurityException) when (_sliceOwnedPaths is { Length: > 0 })
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OsConsoleLog.For<LocalLlmCoder>().LogSwallowed(ex, $"sandbox tool '{name}' failed");
                return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, ex.Message), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload);
            }
        }

        private async Task<string> ApplyRoslynBatchEditAsync(JsonElement args, CancellationToken ct)
        {
            if (!args.TryGetProperty("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
            {
                return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, "roslyn_batch_edit requires an 'edits' array."), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload);
            }

            var edits = new List<WorktreeCSharpEdit>();
            foreach (var item in editsEl.EnumerateArray())
            {
                edits.Add(ParseCSharpEdit(item, RequireString(item, "path")));
            }

            var summary = await _batchOrchestrator!.ApplyAstEditsAsync(_root, edits, _sliceOwnedPaths, ct).ConfigureAwait(false);
            foreach (var touched in summary.TouchedFiles) { if (!string.IsNullOrWhiteSpace(touched)) { _touched.Add(touched); } }
            return JsonSerializer.Serialize(new RoslynBatchResultPayload(summary.Succeeded, summary.TouchedFiles, summary.Diagnostics, summary.Detail), CatalogAotJsonContext.Relaxed.RoslynBatchResultPayload);
        }

        private async Task<string> InspectRoslynSymbolsAsync(string? filter, CancellationToken ct)
        {
            var symbols = await _roslyn.InspectSymbolsAsync(_root, filter, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new RoslynSymbolsPayload(true, symbols.Count, symbols), CatalogAotJsonContext.Relaxed.RoslynSymbolsPayload);
        }

        private async Task<string> InspectRoslynDiagnosticsAsync(string? path, CancellationToken ct)
        {
            string[]? paths = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var (rel, _) = ResolveConfinedPath(path, requireWriteAllowed: false, allowDirectory: false);
                paths = [rel];
            }

            var diagnostics = await _roslyn.AnalyzeAsync(_root, paths, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new RoslynDiagnosticsPayload(true, diagnostics.Count, diagnostics), CatalogAotJsonContext.Relaxed.RoslynDiagnosticsPayload);
        }

        private async Task<string> ApplyRoslynEditAsync(JsonElement args, CancellationToken ct)
        {
            var (rel, _) = ResolveConfinedPath(RequireString(args, "path"), requireWriteAllowed: true, allowDirectory: false);
            EnforceMaxTouchedBudget(rel);
            var summary = await _roslyn.ApplyEditsAsync(_root, [ParseCSharpEdit(args, rel)], ct).ConfigureAwait(false);
            foreach (var touched in summary.TouchedFiles) { _touched.Add(touched); }
            return JsonSerializer.Serialize(new RoslynEditResultPayload(summary.Succeeded, summary.TouchedFiles, summary.Diagnostics, summary.Diff, summary.Detail), CatalogAotJsonContext.Relaxed.RoslynEditResultPayload);
        }

        private static WorktreeCSharpEdit ParseCSharpEdit(JsonElement item, string relativePath)
        {
            var rawKind = RequireString(item, "kind").Trim().ToLowerInvariant();
            var kind = rawKind switch
            {
                "add_using" or "addusing" => WorktreeCSharpEditKind.AddUsing,
                "add_or_replace_member" or "addorreplacemember" or "replace_member" or "add_member" => WorktreeCSharpEditKind.AddOrReplaceMember,
                "remove_member" or "removemember" => WorktreeCSharpEditKind.RemoveMember,
                "create_type_file" or "createtypefile" => WorktreeCSharpEditKind.CreateTypeFile,
                _ => throw new ArgumentException($"Unsupported roslyn edit kind '{rawKind}'."),
            };

            return new WorktreeCSharpEdit(
                Kind: kind,
                RelativePath: relativePath,
                TypeName: GetOptionalString(item, "type_name"),
                MemberName: GetOptionalString(item, "member_name"),
                NamespaceName: GetOptionalString(item, "namespace_name"),
                Source: GetOptionalString(item, "source"),
                TypeKind: GetOptionalString(item, "type_kind"),
                BaseTypes: GetOptionalString(item, "base_types"));
        }

        public string ListFiles(string? relativeDir, string? filter, int maxItems = MaxListedFiles)
        {
            var startDir = string.IsNullOrWhiteSpace(relativeDir) ? _root : ResolveConfinedPath(relativeDir, requireWriteAllowed: false, allowDirectory: true).FullPath;
            if (!Directory.Exists(startDir)) { return $"Directory '{relativeDir}' does not exist."; }

            var results = new List<string>();
            foreach (var file in Directory.EnumerateFiles(startDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(_root, file).Replace('\\', '/');
                if (rel.Split('/').Any(static seg => seg.StartsWith('.') || seg is "bin" or "obj" or "artifacts" or "node_modules")) { continue; }
                if (!string.IsNullOrWhiteSpace(filter) && !rel.Contains(filter, StringComparison.OrdinalIgnoreCase)) { continue; }
                results.Add(rel);
                if (results.Count >= maxItems) { break; }
            }

            return results.Count == 0 ? "(no matching files)" : string.Join("\n", results.OrderBy(p => p, StringComparer.Ordinal));
        }

        public async Task<string> ReadFileAsync(string relativePath, CancellationToken ct)
        {
            var (rel, full) = ResolveConfinedPath(relativePath, requireWriteAllowed: false, allowDirectory: false);
            if (!File.Exists(full)) { return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, $"File '{rel}' does not exist."), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload); }
            var text = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
            return text.Length > MaxReadCharacters ? text[..MaxReadCharacters] + "\n... [truncated]" : text;
        }

        public async Task<string> ReplaceInFileAsync(string relativePath, string oldText, string newText, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(oldText)) { throw new ArgumentException("old_text must not be empty."); }
            var (rel, full) = ResolveConfinedPath(relativePath, requireWriteAllowed: true, allowDirectory: false);
            if (!File.Exists(full)) { throw new FileNotFoundException($"File '{rel}' does not exist."); }

            EnforceMaxTouchedBudget(rel);
            var content = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
            var target = oldText;
            if (!content.Contains(target, StringComparison.Ordinal))
            {
                var crlfTarget = target.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
                if (content.Contains(crlfTarget, StringComparison.Ordinal))
                {
                    target = crlfTarget;
                    newText = newText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
                }
                else
                {
                    throw new InvalidOperationException($"old_text was not found in '{rel}'. Call read_file first to verify exact text.");
                }
            }

            var idx = content.IndexOf(target, StringComparison.Ordinal);
            var updated = string.Concat(content.AsSpan(0, idx), newText, content.AsSpan(idx + target.Length));
            await File.WriteAllTextAsync(full, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
            _touched.Add(rel);
            return JsonSerializer.Serialize(new FileActionPayload(true, rel, "replaced"), CatalogAotJsonContext.Relaxed.FileActionPayload);
        }

        public async Task<string> WriteFileAsync(string relativePath, string content, CancellationToken ct)
        {
            var (rel, full) = ResolveConfinedPath(relativePath, requireWriteAllowed: true, allowDirectory: false);
            EnforceMaxTouchedBudget(rel);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            await File.WriteAllTextAsync(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
            _touched.Add(rel);
            return JsonSerializer.Serialize(new FileActionPayload(true, rel, "written"), CatalogAotJsonContext.Relaxed.FileActionPayload);
        }

        private void EnforceMaxTouchedBudget(string rel)
        {
            if (!_touched.Contains(rel) && _touched.Count >= _policy.MaxChangedFiles)
            {
                throw new SecurityException($"Maximum of {_policy.MaxChangedFiles} modified files reached for this run.");
            }
        }

        internal (string RelativePath, string FullPath) ResolveConfinedPath(string rawPath, bool requireWriteAllowed, bool allowDirectory)
            => _policy.ResolveConfinedPath(_root, rawPath, requireWriteAllowed, allowDirectory);

        private static string RequireString(JsonElement obj, string prop)
            => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : throw new ArgumentException($"Missing required string argument '{prop}'.");

        private static string? GetOptionalString(JsonElement obj, string prop)
            => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
