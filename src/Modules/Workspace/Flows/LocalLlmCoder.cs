using System.ComponentModel;
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
/// and <see cref="InoNativeHarness"/> slice workers. Routes through <see cref="ModelCatalog.ResolveRoleClientAsync"/>
/// and <see cref="Microsoft.Extensions.AI"/> <c>UseFunctionInvocation</c>, confining all file and Roslyn operations
/// inside the worktree via <see cref="ConfinedWorkTreeSandbox"/> and <see cref="ChangePolicy"/>.
/// </summary>
internal sealed class LocalLlmCoder : ICoder
{
    private const int MaxListedFiles = 200;
    private const int MaxReadCharacters = 24_000;

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
        ModelCatalog.ResolvedRoleClient? ExplicitClient = null,
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
            return await RunWithTimeoutAsync(sandbox, loopRequest, explicitClient: null, m => attemptedModelId = m, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Local LLM coder model '{Model}' failed the coding task.", attemptedModelId ?? "unresolved");
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        if (failure.SourceException is not SecurityException
            && _options.EscalateToFrontierOnRepeatedFailure
            && await ResolveFrontierClientAsync(_services, brain: null, _logger, ct).ConfigureAwait(false) is { } frontier)
        {
            using (frontier)
            {
                if (!string.Equals(frontier.Model.Model, attemptedModelId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("frontier-escalation: escalating from local coder model '{LocalModel}' to frontier model '{FrontierModel}'.", attemptedModelId ?? "unresolved", frontier.Model.Model);
                    try
                    {
                        return await RunWithTimeoutAsync(sandbox, loopRequest, frontier, m => attemptedModelId = m, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "frontier-escalation: frontier model '{FrontierModel}' also failed the coding task.", frontier.Model.Model);
                        failure = ExceptionDispatchInfo.Capture(ex);
                    }
                }
            }
        }

        failure.Throw();
        throw new UnreachableException();
    }

    private async Task<CoderEditResult> RunWithTimeoutAsync(
        ConfinedWorkTreeSandbox sandbox,
        ToolLoopRequest request,
        ModelCatalog.ResolvedRoleClient? explicitClient,
        Action<string> onModelResolved,
        CancellationToken ct)
    {
        var timeout = _options.Timeout ?? TimeSpan.FromMinutes(4);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await RunToolLoopAsync(_services, request with { ExplicitClient = explicitClient }, sandbox, onModelResolved, timeoutCts.Token).ConfigureAwait(false);
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
        CancellationToken ct = default)
    {
        var ownedClient = request.ExplicitClient is null;
        var explicitSelection = string.IsNullOrWhiteSpace(request.Options?.PreferredModel)
            ? null
            : new ModelSelection(request.Options.PreferredModel.Trim());

        var resolved = request.ExplicitClient ?? await ModelCatalog.ResolveRoleClientAsync(
            services,
            request.Brain,
            ModelRole.Think,
            explicitSelection,
            requiresTools: true,
            invokesTools: false,
            ollamaContextWindowTokens: request.Options?.ContextWindowTokens,
            rejectDowngradedThink: request.RejectTierDowngrade,
            ct: ct).ConfigureAwait(false);

        try
        {
            onModelResolved?.Invoke(resolved.Model.Model);

            var sandbox = existingSandbox ?? new ConfinedWorkTreeSandbox(
                request.WorktreePath,
                request.Policy,
                request.Roslyn,
                request.SliceOwnedPaths,
                request.BatchOrchestrator);
            var tools = sandbox.CreateAIFunctions();
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, request.SystemPrompt),
                new(ChatRole.User, request.UserPrompt),
            };

            var lastSummary = await ExecuteToolCallingSessionAsync(
                resolved,
                messages,
                tools,
                maxModelCalls: request.MaxToolRounds,
                isWorkDone: () => sandbox.TouchedFiles.Count > 0,
                nudgeMessage: request.NudgePrompt,
                defaultSummary: request.DefaultSummary,
                loggerFactory: services?.GetService<ILoggerFactory>(),
                ct).ConfigureAwait(false);

            if (sandbox.TouchedFiles.Count > 0)
            {
                ValidateTouchedFiles(sandbox.TouchedFiles, request.Policy, request.SliceOwnedPaths);
                return new CoderEditResult(lastSummary, sandbox.TouchedFiles, resolved.Model.Model);
            }

            throw new InvalidOperationException($"{request.ExhaustionMessagePrefix} {request.MaxToolRounds} tool rounds using '{resolved.Model.Model}' without modifying any file.");
        }
        finally
        {
            if (ownedClient)
            {
                resolved.Dispose();
            }
        }
    }

    /// <summary>
    /// Runs a bounded <see cref="Microsoft.Extensions.AI"/> <c>UseFunctionInvocation</c> session over
    /// <paramref name="resolved"/>, nudging the model if it stops without modifying any files while budget remains.
    /// Enforces a strict upper bound of <paramref name="maxModelCalls"/> total model invocations.
    /// </summary>
    internal static async Task<string> ExecuteToolCallingSessionAsync(
        ModelCatalog.ResolvedRoleClient resolved,
        List<ChatMessage> messages,
        IList<AITool> tools,
        int maxModelCalls,
        Func<bool> isWorkDone,
        string nudgeMessage,
        string defaultSummary,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
        var modelCalls = 0;
        var countingClient = new ChatClientBuilder(resolved.Client)
            .Use(async (msgs, opts, next, token) =>
            {
                modelCalls++;
                await next(msgs, opts, token).ConfigureAwait(false);
            })
            .Build();

        var chatOptions = new ChatOptions
        {
            Temperature = resolved.Descriptor.Temperature == CapabilitySupport.Supported ? 0.1f : null,
            Tools = tools,
            ToolMode = ChatToolMode.Auto,
        };

        var lastSummary = defaultSummary;
        while (modelCalls < maxModelCalls)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = maxModelCalls - modelCalls;
            var invokingClient = new ChatClientBuilder(countingClient)
                .UseFunctionInvocation(loggerFactory, cfg =>
                {
                    cfg.MaximumIterationsPerRequest = remaining;
                    cfg.AllowConcurrentInvocation = false;
                    cfg.FunctionInvoker = async (context, token) =>
                    {
                        var result = await context.Function.InvokeAsync(context.Arguments, token).ConfigureAwait(false);
                        if (modelCalls >= maxModelCalls)
                        {
                            context.Terminate = true;
                        }
                        return result;
                    };
                })
                .Build();

            var response = await invokingClient.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);
            messages.AddRange(response.Messages);

            var finalAssistantText = ExtractLastAssistantText(response.Messages);
            if (!string.IsNullOrWhiteSpace(finalAssistantText))
            {
                lastSummary = finalAssistantText;
            }

            if (isWorkDone())
            {
                return lastSummary;
            }

            if (modelCalls < maxModelCalls)
            {
                messages.Add(new ChatMessage(ChatRole.User, nudgeMessage));
            }
        }

        return lastSummary;
    }

    private static string? ExtractLastAssistantText(IList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role != ChatRole.Assistant)
            {
                continue;
            }

            var text = string.Join("\n", msg.Contents.OfType<TextContent>().Select(static t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
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

    internal static async Task<ModelCatalog.ResolvedRoleClient?> ResolveFrontierClientAsync(
        IServiceProvider? services,
        BrainId? brain,
        ILogger logger,
        CancellationToken ct)
    {
        if (services?.GetService<ModelCatalog>() is not { } catalog)
        {
            return null;
        }

        try
        {
            return await catalog.ResolveFrontierClientAsync(
                brain,
                ModelRole.Think,
                requiresTools: true,
                invokesTools: false,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogSwallowed(ex, "frontier model resolution failed; keeping the local outcome");
            return null;
        }
    }

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

    internal static JsonElement ParseJsonToolResult(string? output)
    {
        using var doc = JsonDocument.Parse(EnsureJsonToolResult(output));
        return doc.RootElement.Clone();
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

        public IList<AITool> CreateAIFunctions()
        {
            var extensions = string.Join(", ", _policy.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase));
            var serializerOptions = CatalogAotJsonContext.Relaxed.Options;

            AIFunction CreateTool(Delegate method, string name, string description)
                => AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
                {
                    Name = name,
                    Description = description,
                    SerializerOptions = serializerOptions,
                });

            var tools = new List<AITool>(_batchOrchestrator is not null ? 8 : 7)
            {
                CreateTool(
                    ([Description("Optional relative directory path such as src/Modules")] string? directory = null,
                     [Description("Optional filename or path substring filter")] string? filter = null) =>
                        SafeSyncTool("list_files", () => ListFiles(directory, filter, MaxListedFiles)),
                    "list_files",
                    "List allowed repository files under a relative directory (or optional substring filter). Never accesses outside the worktree."),

                CreateTool(
                    ([Description("Repo-relative file path, e.g. README.md or src/Foo.cs")] string path,
                     CancellationToken ct = default) =>
                        SafeAsyncTool("read_file", () => ReadFileAsync(path, ct)),
                    "read_file",
                    "Read the UTF-8 contents of a single file inside the worktree by repo-relative path."),

                CreateTool(
                    ([Description("Repo-relative file path")] string path,
                     [Description("Exact existing text to replace")] string old_text,
                     [Description("New text")] string new_text,
                     CancellationToken ct = default) =>
                        SafeAsyncTool("replace_in_file", () =>
                        {
                            EnforceOwnedPath(path);
                            return ReplaceInFileAsync(path, old_text, new_text, ct);
                        }),
                    "replace_in_file",
                    "Replace an exact target snippet with replacement text in an existing file inside the worktree."),

                CreateTool(
                    ([Description("Repo-relative file path")] string path,
                     [Description("Full UTF-8 file content")] string content,
                     CancellationToken ct = default) =>
                        SafeAsyncTool("write_file", () =>
                        {
                            EnforceOwnedPath(path);
                            return WriteFileAsync(path, content, ct);
                        }),
                    "write_file",
                    $"Create or overwrite an allowed file ({extensions}) inside the worktree."),

                CreateTool(
                    ([Description("Optional relative path or filename substring filter")] string? filter = null,
                     CancellationToken ct = default) =>
                        SafeAsyncTool("roslyn_symbols", () => InspectRoslynSymbolsAsync(filter, ct)),
                    "roslyn_symbols",
                    "Inspect C# types, namespaces, and members in the worktree via Roslyn syntax analysis."),

                CreateTool(
                    ([Description("Optional repo-relative .cs file path to analyze")] string? path = null,
                     CancellationToken ct = default) =>
                        SafeAsyncTool("roslyn_diagnostics", () => InspectRoslynDiagnosticsAsync(path, ct)),
                    "roslyn_diagnostics",
                    "Run Roslyn C# syntax and semantic diagnostics on worktree C# files."),

                CreateTool(
                    ([Description("add_using | add_or_replace_member | remove_member | create_type_file")] string kind,
                     [Description("Repo-relative .cs path")] string path,
                     string? type_name = null,
                     string? member_name = null,
                     string? namespace_name = null,
                     [Description("Complete C# member declaration or type body")] string? source = null,
                     string? type_kind = null,
                     string? base_types = null,
                     CancellationToken ct = default) =>
                        SafeAsyncTool("roslyn_edit_member", () =>
                        {
                            EnforceOwnedPath(path);
                            return ApplyRoslynEditAsync(kind, path, type_name, member_name, namespace_name, source, type_kind, base_types, ct);
                        }),
                    "roslyn_edit_member",
                    "Apply an AST-validated, Roslyn-formatted C# edit (add_using, add_or_replace_member, remove_member, create_type_file) inside the worktree."),
            };

            if (_batchOrchestrator is not null)
            {
                tools.Add(CreateTool(
                    ([Description("Array of C# AST edit objects")] RoslynBatchEditArg[] edits,
                     CancellationToken ct = default) =>
                        SafeAsyncTool("roslyn_batch_edit", () => ApplyRoslynBatchEditAsync(edits, ct)),
                    "roslyn_batch_edit",
                    "Execute a deterministic multi-edit C# AST batch via InoCodeOrchestrator in a single turn."));
            }

            return tools;
        }

        private void EnforceOwnedPath(string path)
        {
            if (_sliceOwnedPaths is { Length: > 0 })
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(path, _sliceOwnedPaths);
            }
        }

        private JsonElement SafeSyncTool(string name, Func<string> action)
        {
            try
            {
                return ParseJsonToolResult(action());
            }
            catch (SecurityException) when (_sliceOwnedPaths is { Length: > 0 })
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OsConsoleLog.For<LocalLlmCoder>().LogSwallowed(ex, $"sandbox tool '{name}' failed");
                return ParseJsonToolResult(JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, ex.Message), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload));
            }
        }

        private async Task<JsonElement> SafeAsyncTool(string name, Func<Task<string>> action)
        {
            try
            {
                return ParseJsonToolResult(await action().ConfigureAwait(false));
            }
            catch (SecurityException) when (_sliceOwnedPaths is { Length: > 0 })
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OsConsoleLog.For<LocalLlmCoder>().LogSwallowed(ex, $"sandbox tool '{name}' failed");
                return ParseJsonToolResult(JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, ex.Message), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload));
            }
        }

        private async Task<string> ApplyRoslynBatchEditAsync(RoslynBatchEditArg[]? editsArg, CancellationToken ct)
        {
            if (editsArg is null)
            {
                return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, "roslyn_batch_edit requires an 'edits' array."), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload);
            }

            var edits = new List<WorktreeCSharpEdit>(editsArg.Length);
            foreach (var item in editsArg)
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    throw new ArgumentException("Missing required string argument 'path'.");
                }

                edits.Add(BuildCSharpEdit(item.Kind, item.Path, item.TypeName, item.MemberName, item.NamespaceName, item.Source, item.TypeKind, item.BaseTypes));
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

        private async Task<string> ApplyRoslynEditAsync(
            string rawKind,
            string rawPath,
            string? typeName,
            string? memberName,
            string? namespaceName,
            string? source,
            string? typeKind,
            string? baseTypes,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) { throw new ArgumentException("Missing required string argument 'path'."); }
            var (rel, _) = ResolveConfinedPath(rawPath, requireWriteAllowed: true, allowDirectory: false);
            EnforceMaxTouchedBudget(rel);
            var edit = BuildCSharpEdit(rawKind, rel, typeName, memberName, namespaceName, source, typeKind, baseTypes);
            var summary = await _roslyn.ApplyEditsAsync(_root, [edit], ct).ConfigureAwait(false);
            foreach (var touched in summary.TouchedFiles) { _touched.Add(touched); }
            return JsonSerializer.Serialize(new RoslynEditResultPayload(summary.Succeeded, summary.TouchedFiles, summary.Diagnostics, summary.Diff, summary.Detail), CatalogAotJsonContext.Relaxed.RoslynEditResultPayload);
        }

        private static WorktreeCSharpEdit BuildCSharpEdit(
            string rawKind,
            string relativePath,
            string? typeName,
            string? memberName,
            string? namespaceName,
            string? source,
            string? typeKind,
            string? baseTypes)
        {
            if (string.IsNullOrWhiteSpace(rawKind)) { throw new ArgumentException("Missing required string argument 'kind'."); }
            var normalizedKind = rawKind.Trim().ToLowerInvariant();
            var kind = normalizedKind switch
            {
                "add_using" or "addusing" => WorktreeCSharpEditKind.AddUsing,
                "add_or_replace_member" or "addorreplacemember" or "replace_member" or "add_member" => WorktreeCSharpEditKind.AddOrReplaceMember,
                "remove_member" or "removemember" => WorktreeCSharpEditKind.RemoveMember,
                "create_type_file" or "createtypefile" => WorktreeCSharpEditKind.CreateTypeFile,
                _ => throw new ArgumentException($"Unsupported roslyn edit kind '{normalizedKind}'."),
            };

            return new WorktreeCSharpEdit(
                Kind: kind,
                RelativePath: relativePath,
                TypeName: typeName,
                MemberName: memberName,
                NamespaceName: namespaceName,
                Source: source,
                TypeKind: typeKind,
                BaseTypes: baseTypes);
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
    }
}
