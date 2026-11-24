using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
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
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Options controlling <see cref="LocalLlmCoder"/> model selection, tool round budget, timeout, and optional external CLI fallback.
/// </summary>
internal sealed class LocalLlmCoderOptions
{
    /// <summary>Optional preferred model key/tag (e.g. <c>qwen2.5-coder:14b</c>).</summary>
    public string? PreferredModel { get; set; }

    /// <summary>Maximum LLM tool-calling turns per coding task.</summary>
    public int MaxToolRounds { get; set; } = 12;

    /// <summary>Hard wall-clock budget for a coding turn loop before failing with <see cref="TimeoutException"/>.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Requested Ollama context window (<c>num_ctx</c>) in tokens when running against an Ollama model that declares a context limit.</summary>
    public int? ContextWindowTokens { get; set; } = 16384;

    /// <summary>Optional external CLI executable fallback (disabled unless explicitly configured).</summary>
    public string? FallbackCliExecutable { get; set; }

    /// <summary>Arguments template for <see cref="FallbackCliExecutable"/> (<c>{goal}</c> is replaced with the prompt).</summary>
    public string[] FallbackCliArguments { get; set; } = [];

    /// <summary>
    /// When the local model fails a coding task (or exhausts intra-slice repair turns), retry once with a keyed cloud
    /// Think model from <see cref="ModelCatalog.DecideFrontier"/>. Without a cloud key the local outcome stands.
    /// </summary>
    public bool EscalateToFrontierOnRepeatedFailure { get; set; } = true;
}

/// <summary>
/// Local-first <see cref="ICoder"/> implementation that routes through <see cref="Modules.AI"/>
/// (<see cref="ModelCatalog.Decide"/> with <see cref="Role.Think"/>) and <see cref="IModelInference"/>
/// using strictly path-confined file tools (<c>list_files</c>, <c>read_file</c>, <c>replace_in_file</c>, <c>write_file</c>)
/// inside <see cref="WorkTreeLease.TreePath"/>.
/// </summary>
internal sealed class LocalLlmCoder : ICoder
{
    private const int MaxListedFiles = 200;
    private const int MaxReadCharacters = 24_000;

    private static IReadOnlyList<InferenceTool> CreateToolDefinitions(ChangePolicy policy)
    {
        var extensions = string.Join(", ", policy.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase));
        return
        [
            new(
                "list_files",
                "List allowed repository files under a relative directory (or optional substring filter). Never accesses outside the worktree.",
                """{"type":"object","properties":{"directory":{"type":"string","description":"Optional relative directory path such as src/Modules"},"filter":{"type":"string","description":"Optional filename or path substring filter"}}}"""),
            new(
                "read_file",
                "Read the UTF-8 contents of a single file inside the worktree by repo-relative path.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path, e.g. README.md or src/Foo.cs"}},"required":["path"]}"""),
            new(
                "replace_in_file",
                "Replace an exact target snippet with replacement text in an existing file inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path"},"old_text":{"type":"string","description":"Exact existing text to replace"},"new_text":{"type":"string","description":"New text"}},"required":["path","old_text","new_text"]}"""),
            new(
                "write_file",
                $"Create or overwrite an allowed file ({extensions}) inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path"},"content":{"type":"string","description":"Full UTF-8 file content"}},"required":["path","content"]}"""),
            new(
                "roslyn_symbols",
                "Inspect C# types, namespaces, and members in the worktree via Roslyn syntax analysis.",
                """{"type":"object","properties":{"filter":{"type":"string","description":"Optional relative path or filename substring filter"}}}"""),
            new(
                "roslyn_diagnostics",
                "Run Roslyn C# syntax and semantic diagnostics on worktree C# files.",
                """{"type":"object","properties":{"path":{"type":"string","description":"Optional repo-relative .cs file path to analyze"}}}"""),
            new(
                "roslyn_edit_member",
                "Apply an AST-validated, Roslyn-formatted C# edit (add_using, add_or_replace_member, remove_member, create_type_file) inside the worktree.",
                """{"type":"object","properties":{"kind":{"type":"string","description":"add_using | add_or_replace_member | remove_member | create_type_file"},"path":{"type":"string","description":"Repo-relative .cs path"},"type_name":{"type":"string"},"member_name":{"type":"string"},"namespace_name":{"type":"string"},"source":{"type":"string","description":"Complete C# member declaration or type body"}},"required":["kind","path"]}"""),
        ];
    }

    private static string BuildSystemPrompt(ChangePolicy policy) => $"""
        You are Ino's surgical coding engine operating inside an isolated Git worktree.
        Rules:
        {policy.Describe()}
        3. Always inspect existing files with list_files / read_file / roslyn_symbols before editing, unless creating a brand-new file explicitly requested by the goal.
        4. Prefer roslyn_edit_member + roslyn_diagnostics for C# AST-validated edits, and replace_in_file or write_file for Dart (.dart), Markdown (.md), or small file edits.
        5. Once you have made at least one real file modification that satisfies the goal, reply with a concise 1-2 sentence summary of the change and stop calling tools.
        """;

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
        var attempt = new RunnerAttempt();
        ExceptionDispatchInfo failure;
        try
        {
            return await RunWithTimeoutAsync(sandbox, lease, goal, runner: null, attempt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Local LLM coder model '{Model}' failed the coding task.", attempt.ModelId ?? "unresolved");
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        if (failure.SourceException is not SecurityException
            && _options.EscalateToFrontierOnRepeatedFailure
            && await ResolveFrontierRunnerAsync(ct).ConfigureAwait(false) is { } frontier
            && !string.Equals(frontier.ModelId, attempt.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "frontier-escalation: escalating from local coder model '{LocalModel}' to frontier model '{FrontierModel}'.",
                attempt.ModelId ?? "unresolved",
                frontier.ModelId);
            try
            {
                return await RunWithTimeoutAsync(sandbox, lease, goal, frontier, attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "frontier-escalation: frontier model '{FrontierModel}' also failed the coding task.", frontier.ModelId);
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.FallbackCliExecutable))
        {
            _logger.LogWarning(failure.SourceException, "Local LLM coder failed; falling back to configured CLI executable '{Cli}'.", _options.FallbackCliExecutable);
            var cli = new ExternalCliCoder(_options.FallbackCliExecutable, _options.FallbackCliArguments, _options.Timeout);
            return await cli.EditAsync(lease, goal, ct).ConfigureAwait(false);
        }

        failure.Throw();
        throw new UnreachableException();
    }

    private sealed class RunnerAttempt
    {
        public string? ModelId { get; set; }
    }

    private async Task<CoderEditResult> RunWithTimeoutAsync(
        ConfinedWorkTreeSandbox sandbox,
        WorkTreeLease lease,
        string goal,
        IModelTurnRunner? runner,
        RunnerAttempt attempt,
        CancellationToken ct)
    {
        var timeout = _options.Timeout ?? TimeSpan.FromMinutes(4);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await RunModelLoopAsync(sandbox, lease, goal, runner, attempt, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Local LLM coder timed out after {timeout}.");
        }
    }

    private async Task<CoderEditResult> RunModelLoopAsync(
        ConfinedWorkTreeSandbox sandbox,
        WorkTreeLease lease,
        string goal,
        IModelTurnRunner? runner,
        RunnerAttempt attempt,
        CancellationToken ct)
    {
        runner ??= await ResolveModelRunnerAsync(ct).ConfigureAwait(false);
        attempt.ModelId = runner.ModelId;
        var toolDefinitions = CreateToolDefinitions(_policy);
        var initialListing = sandbox.ListFiles(null, null, maxItems: 60);
        var messages = new List<AiMessage>
        {
            new("system", [new AiText(BuildSystemPrompt(_policy))]),
            new("user", [new AiText(
                $"Repository: {lease.RepositoryId} (base branch: {lease.BaseBranch})\n" +
                $"Goal: {goal}\n\n" +
                $"Top-level / initial files in worktree:\n{initialListing}\n\n" +
                "Use the file tools to inspect and apply the requested change now.")]),
        };

        string lastSummary = "Applied requested code changes.";
        for (var round = 0; round < _options.MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await runner.InvokeAsync(messages, toolDefinitions, ct).ConfigureAwait(false);
            var assistantMessage = reply.Messages.LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                ?? reply.Messages.LastOrDefault()
                ?? new AiMessage("assistant", []);
            messages.Add(assistantMessage);

            var toolCalls = assistantMessage.Content.OfType<AiToolCall>().ToArray();
            var textSummary = string.Join("\n", assistantMessage.Content.OfType<AiText>().Select(t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(textSummary))
            {
                lastSummary = textSummary;
            }

            if (toolCalls.Length == 0)
            {
                if (sandbox.TouchedFiles.Count > 0)
                {
                    if (_policy.Check([.. sandbox.TouchedFiles.Select(RepoPath.Parse)]) is { } refusal)
                    {
                        throw new SecurityException(refusal);
                    }

                    return new CoderEditResult(lastSummary, sandbox.TouchedFiles, runner.ModelId);
                }

                // Model answered without calling write_file / replace_in_file yet — nudge it once to use a write tool.
                messages.Add(new AiMessage("user", [new AiText(
                    "No files have been modified yet. Call replace_in_file or write_file to make the requested change on disk, then summarize.")]));
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
            if (_policy.Check([.. sandbox.TouchedFiles.Select(RepoPath.Parse)]) is { } refusal)
            {
                throw new SecurityException(refusal);
            }

            return new CoderEditResult(lastSummary, sandbox.TouchedFiles, runner.ModelId);
        }

        throw new InvalidOperationException(
            $"Coder exhausted {_options.MaxToolRounds} tool rounds using '{runner.ModelId}' without modifying any file.");
    }

    internal static string EnsureJsonToolResult(string? output)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                using var _ = JsonDocument.Parse(output);
                return output;
            }
            catch (JsonException)
            {
                return JsonSerializer.Serialize(new ToolContentPayload(true, output), CatalogAotJsonContext.Relaxed.ToolContentPayload);
            }
        }

        return JsonSerializer.Serialize(new ToolContentPayload(true, output ?? string.Empty), CatalogAotJsonContext.Relaxed.ToolContentPayload);
    }

    private async Task<IModelTurnRunner> ResolveModelRunnerAsync(CancellationToken ct)
    {
        var catalog = _services?.GetService<ModelCatalog>();
        var inference = _services?.GetService<IModelInference>();
        var explicitSelection = string.IsNullOrWhiteSpace(_options.PreferredModel)
            ? null
            : new ModelSelection(_options.PreferredModel.Trim());

        if (catalog is not null)
        {
            var resolved = await catalog.Decide(brainId: null, ModelRole.Think, explicitSelection, requiresTools: true, ct).ConfigureAwait(false);
            if (resolved.Provider == AiProvider.Host && _services?.GetService<IChatClient>() is null)
            {
                throw new NoProviderConfiguredException("No configured tool-capable AI model or local Ollama server is available for Feature-to-PR coding.");
            }

            if (explicitSelection is null
                && resolved.Profile is null
                && KnownModels.Find(resolved.Model) is { Tier: not Tier.Deep } downgraded)
            {
                var expectedThink = KnownModels.Defaults.Device.TryGetValue(ModelRole.Think, out var thinkId)
                    ? thinkId.Value
                    : KnownModels.Qwen25Coder14B.Id.Value;
                throw new NoProviderConfiguredException(
                    $"No Think/Deep model (such as '{expectedThink}') is available for coding; refusing to silently downgrade to '{downgraded.Id.Value}' ({downgraded.Tier} tier). Pull '{expectedThink}' in Ollama, configure a Think model, or set IntoChatOS:Coder:Model explicitly.");
            }

            var effectiveInference = inference ?? throw new InvalidOperationException("IModelInference must be registered alongside ModelCatalog.");
            var selection = ToSelection(resolved);
            var desc = await effectiveInference.Describe(selection, model: null, cancellationToken: ct, brainId: null).ConfigureAwait(false);
            var ollamaCtx = ResolveOllamaContextWindow(resolved.Provider == AiProvider.Ollama, desc);
            return new InferenceServiceRunner(effectiveInference, selection, resolved.Model, desc.Temperature == CapabilitySupport.Supported, ollamaCtx);
        }

        if (inference is not null)
        {
            var candidates = new List<ModelSelection?>();
            if (explicitSelection is not null)
            {
                candidates.Add(explicitSelection);
            }
            candidates.Add(null);

            foreach (var selection in candidates)
            {
                try
                {
                    var desc = await inference.Describe(selection, model: null, cancellationToken: ct, brainId: null).ConfigureAwait(false);
                    if (desc.Tools == CapabilitySupport.Supported
                        && (!string.Equals(desc.Provider, "Host", StringComparison.OrdinalIgnoreCase)
                            || _services?.GetService<IChatClient>() is not null))
                    {
                        var isOllama = string.Equals(desc.Provider, nameof(AiProvider.Ollama), StringComparison.OrdinalIgnoreCase);
                        var ollamaCtx = ResolveOllamaContextWindow(isOllama, desc);
                        return new InferenceServiceRunner(inference, selection, desc.Model, desc.Temperature == CapabilitySupport.Supported, ollamaCtx);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is ArgumentException or InvalidOperationException or NotSupportedException or ProviderUnavailableException)
                {
                    _logger.LogWarning(ex, "Configured IModelInference candidate '{Model}' could not resolve.", selection?.Model ?? "default");
                }
            }
        }

        throw new InvalidOperationException("No configured tool-capable AI model or local Ollama server is available for Feature-to-PR coding.");
    }

    private async Task<IModelTurnRunner?> ResolveFrontierRunnerAsync(CancellationToken ct)
    {
        if (_services?.GetService<ModelCatalog>() is not { } catalog || _services.GetService<IModelInference>() is not { } inference)
        {
            return null;
        }

        try
        {
            if (await catalog.DecideFrontier(brain: null, ModelRole.Think, requiresTools: true, ct).ConfigureAwait(false) is not { } frontier)
            {
                return null;
            }

            var selection = ToSelection(frontier);
            var desc = await inference.Describe(selection, model: null, cancellationToken: ct, brainId: null).ConfigureAwait(false);
            return new InferenceServiceRunner(inference, selection, frontier.Model, desc.Temperature == CapabilitySupport.Supported, ollamaContextWindowTokens: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "frontier model resolution failed; keeping the local outcome");
            return null;
        }
    }

    private int? ResolveOllamaContextWindow(bool isOllama, ModelDescriptor desc)
        => isOllama
            && _options.ContextWindowTokens is { } requestedCtx and > 0
            && desc.ContextWindowTokens is { } maxCtx and > 0
            ? (int)Math.Min(requestedCtx, maxCtx)
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
        if (configuration["IntoChatOS:Coder:CliExecutable"] is { Length: > 0 } cli)
        {
            opts.FallbackCliExecutable = cli;
        }
        if (int.TryParse(configuration["IntoChatOS:Coder:MaxToolRounds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRounds) && maxRounds > 0)
        {
            opts.MaxToolRounds = maxRounds;
        }
        if (double.TryParse(configuration["IntoChatOS:Coder:TimeoutSeconds"], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            opts.Timeout = TimeSpan.FromSeconds(seconds);
        }
        if (int.TryParse(configuration["IntoChatOS:Coder:ContextWindowTokens"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var contextWindow) && contextWindow > 0)
        {
            opts.ContextWindowTokens = contextWindow;
        }
        if (bool.TryParse(configuration["IntoChatOS:Coder:EscalateToFrontier"], out var escalate))
        {
            opts.EscalateToFrontierOnRepeatedFailure = escalate;
        }

        return opts;
    }

    private interface IModelTurnRunner
    {
        string ModelId { get; }
        Task<InferenceResult> InvokeAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<InferenceTool> tools, CancellationToken ct);
    }

    private sealed class InferenceServiceRunner(
        IModelInference inference,
        ModelSelection? selection,
        string modelId,
        bool supportsTemperature,
        int? ollamaContextWindowTokens) : IModelTurnRunner
    {
        public string ModelId => modelId;

        public Task<InferenceResult> InvokeAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<InferenceTool> tools, CancellationToken ct)
        {
            var temperature = supportsTemperature ? 0.1f : (float?)null;
            var provider = ollamaContextWindowTokens is { } ctx
                ? new OllamaInferenceOptions(ContextWindowTokens: ctx)
                : null;
            var options = temperature is not null || provider is not null
                ? new InferenceOptions(Temperature: temperature, Provider: provider)
                : null;
            var req = new InferenceRequest(messages, Model: selection, Options: options, Tools: tools);
            return inference.Generate(req, model: null, cancellationToken: ct, brainId: null);
        }
    }

    /// <summary>
    /// Strictly sandboxed file tool executor confined to <c>Path.GetFullPath(treeRoot)</c> via <see cref="ChangePolicy"/>.
    /// Blocks <c>..</c> traversal, symlinks/reparse points, dotfiles (<c>.git</c>, <c>.github</c>, <c>.claude</c>),
    /// agent instruction files (<c>CLAUDE.md</c>, <c>AGENTS.md</c>, <c>SKILL.md</c>), and disallowed extensions on write.
    /// </summary>
    internal sealed class ConfinedWorkTreeSandbox
    {
        private readonly string _root;
        private readonly ChangePolicy _policy;
        private readonly IWorktreeRoslyn _roslyn;
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

        public ConfinedWorkTreeSandbox(string treeRoot, ChangePolicy? policy = null, IWorktreeRoslyn? roslyn = null)
        {
            _root = Path.GetFullPath(treeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _policy = policy ?? ChangePolicy.Default;
            _roslyn = roslyn ?? UnavailableWorktreeRoslyn.Instance;
        }

        public IReadOnlyList<string> TouchedFiles => [.. _touched.OrderBy(p => p, StringComparer.Ordinal)];

        internal void RecordTouchedFiles(IEnumerable<string> relativePaths)
        {
            foreach (var rel in relativePaths)
            {
                if (!string.IsNullOrWhiteSpace(rel))
                {
                    _touched.Add(rel);
                }
            }
        }

        public async Task<string> ExecuteToolAsync(string name, string argumentsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                var args = doc.RootElement;

                return name switch
                {
                    "list_files" => ListFiles(
                        GetOptionalString(args, "directory"),
                        GetOptionalString(args, "filter"),
                        MaxListedFiles),
                    "read_file" => await ReadFileAsync(RequireString(args, "path"), ct).ConfigureAwait(false),
                    "replace_in_file" => await ReplaceInFileAsync(
                        RequireString(args, "path"),
                        RequireString(args, "old_text"),
                        RequireString(args, "new_text"),
                        ct).ConfigureAwait(false),
                    "write_file" => await WriteFileAsync(
                        RequireString(args, "path"),
                        RequireString(args, "content"),
                        ct).ConfigureAwait(false),
                    "roslyn_symbols" => await InspectRoslynSymbolsAsync(
                        GetOptionalString(args, "filter"),
                        ct).ConfigureAwait(false),
                    "roslyn_diagnostics" => await InspectRoslynDiagnosticsAsync(
                        GetOptionalString(args, "path"),
                        ct).ConfigureAwait(false),
                    "roslyn_edit_member" => await ApplyRoslynEditAsync(args, ct).ConfigureAwait(false),
                    _ => JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, $"Unknown tool '{name}'."), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OsConsoleLog.For<LocalLlmCoder>().LogSwallowed(ex, $"sandbox tool '{name}' failed");
                return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, ex.Message), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload);
            }
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
            var rawPath = RequireString(args, "path");
            var (rel, _) = ResolveConfinedPath(rawPath, requireWriteAllowed: true, allowDirectory: false);
            EnforceMaxTouchedBudget(rel);

            var rawKind = RequireString(args, "kind").Trim().ToLowerInvariant();
            var kind = rawKind switch
            {
                "add_using" or "addusing" => WorktreeCSharpEditKind.AddUsing,
                "add_or_replace_member" or "addorreplacemember" or "replace_member" or "add_member" => WorktreeCSharpEditKind.AddOrReplaceMember,
                "remove_member" or "removemember" => WorktreeCSharpEditKind.RemoveMember,
                "create_type_file" or "createtypefile" => WorktreeCSharpEditKind.CreateTypeFile,
                _ => throw new ArgumentException($"Unsupported roslyn_edit_member kind '{rawKind}'."),
            };

            var edit = new WorktreeCSharpEdit(
                Kind: kind,
                RelativePath: rel,
                TypeName: GetOptionalString(args, "type_name"),
                MemberName: GetOptionalString(args, "member_name"),
                NamespaceName: GetOptionalString(args, "namespace_name"),
                Source: GetOptionalString(args, "source"),
                TypeKind: GetOptionalString(args, "type_kind"),
                BaseTypes: GetOptionalString(args, "base_types"));

            var summary = await _roslyn.ApplyEditsAsync(_root, [edit], ct).ConfigureAwait(false);
            foreach (var touched in summary.TouchedFiles)
            {
                _touched.Add(touched);
            }

            return JsonSerializer.Serialize(
                new RoslynEditResultPayload(summary.Succeeded, summary.TouchedFiles, summary.Diagnostics, summary.Diff, summary.Detail),
                CatalogAotJsonContext.Relaxed.RoslynEditResultPayload);
        }

        public string ListFiles(string? relativeDir, string? filter, int maxItems = MaxListedFiles)
        {
            var startDir = string.IsNullOrWhiteSpace(relativeDir)
                ? _root
                : ResolveConfinedPath(relativeDir, requireWriteAllowed: false, allowDirectory: true).FullPath;

            if (!Directory.Exists(startDir))
            {
                return $"Directory '{relativeDir}' does not exist.";
            }

            var results = new List<string>();
            foreach (var file in Directory.EnumerateFiles(startDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(_root, file).Replace('\\', '/');
                if (rel.Split('/').Any(seg => seg.StartsWith('.') || seg is "bin" or "obj" or "artifacts" or "node_modules"))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(filter) && !rel.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(rel);
                if (results.Count >= maxItems) { break; }
            }

            return results.Count == 0
                ? "(no matching files)"
                : string.Join("\n", results.OrderBy(p => p, StringComparer.Ordinal));
        }

        public async Task<string> ReadFileAsync(string relativePath, CancellationToken ct)
        {
            var (rel, full) = ResolveConfinedPath(relativePath, requireWriteAllowed: false, allowDirectory: false);
            if (!File.Exists(full))
            {
                return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, $"File '{rel}' does not exist."), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload);
            }

            var text = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
            if (text.Length > MaxReadCharacters)
            {
                text = text[..MaxReadCharacters] + "\n... [truncated]";
            }
            return text;
        }

        public async Task<string> ReplaceInFileAsync(string relativePath, string oldText, string newText, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(oldText))
            {
                throw new ArgumentException("old_text must not be empty.");
            }

            var (rel, full) = ResolveConfinedPath(relativePath, requireWriteAllowed: true, allowDirectory: false);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"File '{rel}' does not exist.");
            }

            EnforceMaxTouchedBudget(rel);
            var content = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
            var normalizedContent = content;
            var target = oldText;

            if (!normalizedContent.Contains(target, StringComparison.Ordinal))
            {
                // Handle CRLF vs LF mismatch gracefully
                var crlfTarget = target.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
                if (normalizedContent.Contains(crlfTarget, StringComparison.Ordinal))
                {
                    target = crlfTarget;
                    newText = newText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
                }
                else
                {
                    throw new InvalidOperationException($"old_text was not found in '{rel}'. Call read_file first to verify exact text.");
                }
            }

            var idx = normalizedContent.IndexOf(target, StringComparison.Ordinal);
            var updated = string.Concat(normalizedContent.AsSpan(0, idx), newText, normalizedContent.AsSpan(idx + target.Length));
            await File.WriteAllTextAsync(full, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
            _touched.Add(rel);
            return JsonSerializer.Serialize(new FileActionPayload(true, rel, "replaced"), CatalogAotJsonContext.Relaxed.FileActionPayload);
        }

        public async Task<string> WriteFileAsync(string relativePath, string content, CancellationToken ct)
        {
            var (rel, full) = ResolveConfinedPath(relativePath, requireWriteAllowed: true, allowDirectory: false);
            EnforceMaxTouchedBudget(rel);

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
            _touched.Add(rel);
            return JsonSerializer.Serialize(new FileActionPayload(true, rel, "written"), CatalogAotJsonContext.Relaxed.FileActionPayload);
        }

        private void EnforceMaxTouchedBudget(string rel)
        {
            if (!_touched.Contains(rel) && _touched.Count >= _policy.MaxChangedFiles)
            {
                throw new SecurityException($"Maximum of {_policy.MaxChangedFiles} modified files reached for this Feature-to-PR run.");
            }
        }

        internal (string RelativePath, string FullPath) ResolveConfinedPath(string rawPath, bool requireWriteAllowed, bool allowDirectory)
            => _policy.ResolveConfinedPath(_root, rawPath, requireWriteAllowed, allowDirectory);

        private static string RequireString(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : throw new ArgumentException($"Missing required string argument '{prop}'.");

        private static string? GetOptionalString(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}

/// <summary>
/// Pluggable external CLI coder fallback (e.g. a local script or CLI tool) executed inside <see cref="WorkTreeLease.TreePath"/>
/// with <c>CreateNoWindow = true</c>. Output is still gated by <see cref="IWorkTree.VerifyAndCommitAsync"/> (Triple Git Lock + C# Path Gate).
/// </summary>
internal sealed class ExternalCliCoder(string executable, IReadOnlyList<string>? argumentsTemplate = null, TimeSpan? timeout = null) : ICoder
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(3);

    public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = lease.TreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var template = argumentsTemplate is { Count: > 0 } ? argumentsTemplate : [goal];
        foreach (var item in template)
        {
            psi.ArgumentList.Add(item.Replace("{goal}", goal, StringComparison.Ordinal));
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external CLI coder '{executable}'.");
        }
        process.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdOut = await outTask.ConfigureAwait(false);
            var stdErr = await errTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"External CLI coder '{executable}' exited with code {process.ExitCode}: {stdErr}");
            }

            var summary = string.IsNullOrWhiteSpace(stdOut) ? $"Edited via {Path.GetFileName(executable)}." : stdOut.Trim();
            return new CoderEditResult(summary, [], $"cli:{Path.GetFileName(executable)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw new TimeoutException($"External CLI coder '{executable}' timed out after {_timeout}.");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            OsConsoleLog.For<ExternalCliCoder>().LogDebug(ex, "External CLI coder process already exited before termination.");
        }
    }
}
