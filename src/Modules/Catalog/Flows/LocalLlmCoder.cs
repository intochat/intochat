using System.Collections.Frozen;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Options controlling <see cref="LocalLlmCoder"/> model selection, tool round budget, and optional external CLI fallback.
/// </summary>
public sealed class LocalLlmCoderOptions
{
    /// <summary>Optional preferred model key/tag (e.g. <c>qwen2.5-coder:14b</c>).</summary>
    public string? PreferredModel { get; set; }

    /// <summary>Local Ollama HTTP base address used when no cloud/host model is configured.</summary>
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";

    /// <summary>Maximum LLM tool-calling turns per coding task.</summary>
    public int MaxToolRounds { get; set; } = 12;

    /// <summary>Optional external CLI executable fallback (disabled unless explicitly configured).</summary>
    public string? FallbackCliExecutable { get; set; }

    /// <summary>Arguments template for <see cref="FallbackCliExecutable"/> (<c>{goal}</c> is replaced with the prompt).</summary>
    public string[] FallbackCliArguments { get; set; } = [];
}

/// <summary>
/// Local-first <see cref="ICoder"/> implementation that drives <see cref="IModelInference"/> (or direct local Ollama
/// <c>qwen2.5-coder:14b</c> on RTX 5080 with zero cloud API keys) using strictly path-confined file tools
/// (<c>list_files</c>, <c>read_file</c>, <c>replace_in_file</c>, <c>write_file</c>) inside <see cref="WorkTreeLease.TreePath"/>.
/// Supports an optional <see cref="ExternalCliCoder"/> fallback when configured.
/// </summary>
public sealed class LocalLlmCoder : ICoder
{
    private const int MaxListedFiles = 200;
    private const int MaxReadCharacters = 24_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly FrozenSet<string> AllowedExtensions = new[]
    {
        ".cs",
        ".md",
        ".dart",
        ".txt",
        ".xaml",
        ".svg",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> BlockedFileNames = new[]
    {
        "claude.md",
        "agents.md",
        "skill.md",
        "copilot-instructions.md",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<InferenceTool> ToolDefinitions =
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
            "Create or overwrite an allowed file (.cs, .md, .dart, .txt, .xaml, .svg) inside the worktree.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Repo-relative file path"},"content":{"type":"string","description":"Full UTF-8 file content"}},"required":["path","content"]}"""),
    ];

    private const string SystemPrompt = """
        You are Ino's surgical coding engine operating inside an isolated Git worktree.
        Rules:
        1. Modify at most 5 files total, and ONLY touch files directly needed for the goal.
        2. Allowed extensions ONLY: .cs, .md, .dart, .txt, .xaml, .svg. NEVER touch dotfiles (.git, .github, .claude) or instruction files (CLAUDE.md, AGENTS.md, SKILL.md).
        3. Always inspect existing files with list_files / read_file before editing, unless creating a brand-new file explicitly requested by the goal.
        4. Prefer replace_in_file for surgical edits to existing files, or write_file when creating a new file or rewriting a small file cleanly.
        5. Once you have made at least one real file modification that satisfies the goal, reply with a concise 1-2 sentence summary of the change and stop calling tools.
        """;

    private readonly IServiceProvider? _services;
    private readonly LocalLlmCoderOptions _options;
    private readonly ILogger<LocalLlmCoder> _logger;

    public LocalLlmCoder(
        IServiceProvider? services = null,
        IConfiguration? configuration = null,
        LocalLlmCoderOptions? options = null,
        ILogger<LocalLlmCoder>? logger = null)
    {
        _services = services;
        _logger = logger ?? NullLogger<LocalLlmCoder>.Instance;
        _options = options ?? BindOptions(configuration);
    }

    public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var sandbox = new ConfinedWorkTreeSandbox(lease.TreePath);
        try
        {
            return await RunModelLoopAsync(sandbox, lease, goal, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !string.IsNullOrWhiteSpace(_options.FallbackCliExecutable))
        {
            _logger.LogWarning(ex, "Local LLM coder failed; falling back to configured CLI executable '{Cli}'.", _options.FallbackCliExecutable);
            var cli = new ExternalCliCoder(_options.FallbackCliExecutable!, _options.FallbackCliArguments);
            return await cli.EditAsync(lease, goal, ct).ConfigureAwait(false);
        }
    }

    private async Task<CoderEditResult> RunModelLoopAsync(
        ConfinedWorkTreeSandbox sandbox,
        WorkTreeLease lease,
        string goal,
        CancellationToken ct)
    {
        var runner = await ResolveModelRunnerAsync(ct).ConfigureAwait(false);
        var initialListing = sandbox.ListFiles(null, null, maxItems: 60);
        var messages = new List<AiMessage>
        {
            new("system", [new AiText(SystemPrompt)]),
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
            var reply = await runner.InvokeAsync(messages, ToolDefinitions, ct).ConfigureAwait(false);
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
                toolResults.Add(new AiToolResult(call.CallId, output));
            }

            messages.Add(new AiMessage("tool", [.. toolResults]));
        }

        if (sandbox.TouchedFiles.Count > 0)
        {
            return new CoderEditResult(lastSummary, sandbox.TouchedFiles, runner.ModelId);
        }

        throw new InvalidOperationException(
            $"Coder exhausted {_options.MaxToolRounds} tool rounds using '{runner.ModelId}' without modifying any file.");
    }

    private async Task<IModelTurnRunner> ResolveModelRunnerAsync(CancellationToken ct)
    {
        var inference = _services?.GetService<IModelInference>();
        if (inference is not null)
        {
            // 1. Try explicit PreferredModel or default configured inference model
            var candidates = new List<ModelSelection?>();
            if (!string.IsNullOrWhiteSpace(_options.PreferredModel))
            {
                candidates.Add(new ModelSelection(_options.PreferredModel));
            }
            candidates.Add(null);

            foreach (var selection in candidates)
            {
                try
                {
                    var desc = await inference.Describe(selection, marker: null, cancellationToken: ct, brainId: null).ConfigureAwait(false);
                    // AiProvider.Host with "default" when no IChatClient is registered throws on CreateClient; also require tool support for coding!
                    if (desc.Tools == CapabilitySupport.Supported
                        && (!string.Equals(desc.Provider, "Host", StringComparison.OrdinalIgnoreCase)
                            || _services?.GetService<global::Microsoft.Extensions.AI.IChatClient>() is not null))
                    {
                        return new InferenceServiceRunner(inference, selection, desc.Model, desc.Temperature == CapabilitySupport.Supported);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, "Configured IModelInference candidate not ready; probing local Ollama fallback.");
                }
            }
        }

        // 2. Direct zero-key local Ollama probe (e.g. RTX 5080 running qwen2.5-coder:14b at http://127.0.0.1:11434)
        var ollamaTag = await ProbeLocalOllamaModelAsync(_options.OllamaEndpoint, _options.PreferredModel, ct).ConfigureAwait(false);
        if (ollamaTag is not null)
        {
            return new DirectOllamaChatRunner(_options.OllamaEndpoint, ollamaTag);
        }

        throw new InvalidOperationException(
            $"No configured tool-capable AI model or local Ollama server ({_options.OllamaEndpoint}) is available for Feature-to-PR coding.");
    }

    internal static async Task<string?> ProbeLocalOllamaModelAsync(string endpoint, string? preferred, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync("/api/tags", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var available = models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToArray();
            if (available.Length == 0)
            {
                return null;
            }

            // Priority order: explicit preferred -> qwen2.5-coder:14b -> any qwen*coder* -> qwen3.5* -> first non-embedding model
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var cleanPref = preferred.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase) || preferred.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase)
                    ? preferred[7..]
                    : preferred;
                var exact = available.FirstOrDefault(m => string.Equals(m, cleanPref, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) { return exact; }
            }

            string[] priorities = ["qwen2.5-coder:14b", "qwen2.5-coder", "qwen3-coder", "qwen3.5:4b", "qwen3.5"];
            foreach (var p in priorities)
            {
                var hit = available.FirstOrDefault(m => m.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) { return hit; }
            }

            return available.FirstOrDefault(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase)) ?? available[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(ex, "Local Ollama model probe did not succeed.");
            return null;
        }
    }

    private static LocalLlmCoderOptions BindOptions(IConfiguration? configuration)
    {
        var opts = new LocalLlmCoderOptions();
        if (configuration is null) { return opts; }

        opts.PreferredModel = configuration["IntoChatOS:Coder:Model"] ?? configuration["AI:Ollama:CoderModel"];
        if (configuration["AI:Ollama:Endpoint"] is { Length: > 0 } ep)
        {
            opts.OllamaEndpoint = ep;
        }
        else if (Environment.GetEnvironmentVariable("IntoChatOS__AI__Ollama__Endpoint") is { Length: > 0 } envEp)
        {
            opts.OllamaEndpoint = envEp;
        }

        if (configuration["IntoChatOS:Coder:CliExecutable"] is { Length: > 0 } cli)
        {
            opts.FallbackCliExecutable = cli;
        }

        return opts;
    }

    private interface IModelTurnRunner
    {
        string ModelId { get; }
        Task<InferenceResult> InvokeAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<InferenceTool> tools, CancellationToken ct);
    }

    private sealed class InferenceServiceRunner(IModelInference inference, ModelSelection? selection, string modelId, bool supportsTemperature) : IModelTurnRunner
    {
        public string ModelId => modelId;

        public Task<InferenceResult> InvokeAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<InferenceTool> tools, CancellationToken ct)
        {
            var options = supportsTemperature ? new InferenceOptions(Temperature: 0.1f) : null;
            var req = new InferenceRequest(messages, Model: selection, Options: options, Tools: tools);
            return inference.Generate(req, marker: null, cancellationToken: ct, brainId: null);
        }
    }

    private sealed class DirectOllamaChatRunner(string endpoint, string modelTag) : IModelTurnRunner
    {
        public string ModelId => $"ollama/{modelTag}";

        public async Task<InferenceResult> InvokeAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<InferenceTool> tools, CancellationToken ct)
        {
            // Call Ollama's native /api/chat directly with think:false & tool declarations so zero external provider config is required
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(4) };

            var wireMessages = new List<object>(messages.Count);
            foreach (var msg in messages)
            {
                if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var tr in msg.Content.OfType<AiToolResult>())
                    {
                        wireMessages.Add(new
                        {
                            role = "tool",
                            content = tr.ResultJson,
                        });
                    }
                    continue;
                }

                var text = string.Join("\n", msg.Content.OfType<AiText>().Select(t => t.Text));
                var calls = msg.Content.OfType<AiToolCall>().Select(tc => new
                {
                    function = new
                    {
                        name = tc.Name,
                        arguments = ParseJsonOrEmptyObject(tc.ArgumentsJson),
                    },
                }).ToArray();

                if (calls.Length > 0)
                {
                    wireMessages.Add(new { role = msg.Role, content = text, tool_calls = calls });
                }
                else
                {
                    wireMessages.Add(new { role = msg.Role, content = text });
                }
            }

            var wireTools = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = ParseJsonOrEmptyObject(t.ParametersJson),
                },
            }).ToArray();

            var payload = JsonSerializer.Serialize(new
            {
                model = modelTag,
                stream = false,
                think = false,
                options = new { temperature = 0.1 },
                messages = wireMessages,
                tools = wireTools,
            }, Json);

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("/api/chat", content, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Ollama /api/chat returned {(int)response.StatusCode}: {responseBody}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var msgEl = doc.RootElement.GetProperty("message");
            var parts = new List<AiContent>();
            if (msgEl.TryGetProperty("content", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                var s = textEl.GetString();
                if (!string.IsNullOrEmpty(s)) { parts.Add(new AiText(s)); }
            }

            if (msgEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
            {
                var idx = 0;
                foreach (var item in tcEl.EnumerateArray())
                {
                    if (!item.TryGetProperty("function", out var fn)) { continue; }
                    var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var argsJson = fn.TryGetProperty("arguments", out var a)
                        ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText())
                        : "{}";
                    parts.Add(new AiToolCall($"call_{idx++}_{Guid.NewGuid():N}"[..16], name, argsJson));
                }
            }

            return new InferenceResult(
                [new AiMessage("assistant", [.. parts])],
                FinishReason: parts.OfType<AiToolCall>().Any() ? "tool_calls" : "stop",
                Usage: null,
                ResponseId: null,
                Model: modelTag);
        }

        private static JsonElement ParseJsonOrEmptyObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return JsonElement.Parse("{}"); }
            try { return JsonElement.Parse(raw); }
            catch (JsonException) { return JsonElement.Parse("{}"); }
        }
    }

    /// <summary>
    /// Strictly sandboxed file tool executor confined to <c>Path.GetFullPath(treeRoot)</c>.
    /// Blocks <c>..</c> traversal, symlinks/reparse points, dotfiles (<c>.git</c>, <c>.github</c>, <c>.claude</c>),
    /// agent instruction files (<c>CLAUDE.md</c>, <c>AGENTS.md</c>, <c>SKILL.md</c>), and disallowed extensions on write.
    /// </summary>
    internal sealed class ConfinedWorkTreeSandbox
    {
        private readonly string _root;
        private readonly string _rootWithSep;
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

        public ConfinedWorkTreeSandbox(string treeRoot)
        {
            _root = Path.GetFullPath(treeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _rootWithSep = _root + Path.DirectorySeparatorChar;
        }

        public IReadOnlyList<string> TouchedFiles => [.. _touched.OrderBy(p => p, StringComparer.Ordinal)];

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
                    _ => JsonSerializer.Serialize(new { ok = false, error = $"Unknown tool '{name}'." }),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
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
                return JsonSerializer.Serialize(new { ok = false, error = $"File '{rel}' does not exist." });
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
            return JsonSerializer.Serialize(new { ok = true, path = rel, action = "replaced" });
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
            return JsonSerializer.Serialize(new { ok = true, path = rel, action = "written" });
        }

        private void EnforceMaxTouchedBudget(string rel)
        {
            if (!_touched.Contains(rel) && _touched.Count >= 5)
            {
                throw new SecurityException("Maximum of 5 modified files reached for this Feature-to-PR run.");
            }
        }

        internal (string RelativePath, string FullPath) ResolveConfinedPath(string rawPath, bool requireWriteAllowed, bool allowDirectory)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                throw new SecurityException("Path must not be empty.");
            }

            var normalized = rawPath.Replace('\\', '/').Trim();
            if (normalized is "." && allowDirectory)
            {
                return ("", _root);
            }

            if (Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
            {
                throw new SecurityException($"Absolute paths are forbidden: '{rawPath}'.");
            }

            var segments = normalized.Split('/', StringSplitOptions.None);
            foreach (var seg in segments)
            {
                if (seg.Length == 0 || seg is "." or "..")
                {
                    throw new SecurityException($"Path traversal segment forbidden in '{rawPath}'.");
                }
                if (seg.StartsWith('.'))
                {
                    throw new SecurityException($"Access to dotfile or hidden directory '{rawPath}' (including .git) is forbidden.");
                }
            }

            var leaf = segments[^1];
            if (!allowDirectory && BlockedFileNames.Contains(leaf))
            {
                throw new SecurityException($"Access to agent instruction file '{rawPath}' is forbidden.");
            }

            if (requireWriteAllowed)
            {
                var ext = Path.GetExtension(leaf);
                if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
                {
                    throw new SecurityException(
                        $"Writing extension '{ext}' on '{rawPath}' is forbidden. Allowed extensions: {string.Join(", ", AllowedExtensions.Order())}.");
                }
            }

            var full = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(_rootWithSep, cmp) && !(allowDirectory && string.Equals(full, _root, cmp)))
            {
                throw new SecurityException($"Path '{rawPath}' escapes worktree root.");
            }

            // Reject symlinks/junctions anywhere along the path
            var cursor = full;
            while (!string.IsNullOrEmpty(cursor) && !string.Equals(cursor, _root, cmp))
            {
                if (File.Exists(cursor) || Directory.Exists(cursor))
                {
                    var info = new FileInfo(cursor);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                    {
                        throw new SecurityException($"Symlink or reparse point rejected at '{rawPath}'.");
                    }
                }
                cursor = Path.GetDirectoryName(cursor);
            }

            return (normalized, full);
        }

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
public sealed class ExternalCliCoder(string executable, IReadOnlyList<string>? argumentsTemplate = null, TimeSpan? timeout = null) : ICoder
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
        var stdOut = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        var stdErr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"External CLI coder '{executable}' exited with code {process.ExitCode}: {stdErr}");
        }

        var summary = string.IsNullOrWhiteSpace(stdOut) ? $"Edited via {Path.GetFileName(executable)}." : stdOut.Trim();
        return new CoderEditResult(summary, [], $"cli:{Path.GetFileName(executable)}");
    }
}
