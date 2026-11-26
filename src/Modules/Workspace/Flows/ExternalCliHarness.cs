using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Optional secondary <see cref="IEngineeringHarness"/> adapter wrapping confined <c>claude</c>
/// (<c>-p ... --output-format stream-json</c>) and <c>codex</c> (<c>exec --json ...</c>) CLIs via
/// <see cref="ISandboxCommandRunner"/>, with <c>.worktreeinclude</c> sync, capability probing, and
/// identical <see cref="OperationProgressed"/> progress signals for side-by-side benchmarking against <see cref="InoNativeHarness"/>.
/// </summary>
internal sealed class ExternalCliHarness : IEngineeringHarness
{
    private readonly EngineeringHarnessKind _kind;
    private readonly ISandboxCommandRunner _sandboxRunner;
    private readonly InoCodeOrchestrator _orchestrator;
    private readonly IConfiguration? _configuration;
    private readonly ILogger _logger;

    public ExternalCliHarness(
        EngineeringHarnessKind kind,
        ISandboxCommandRunner? sandboxRunner = null,
        IWorktreeRoslyn? roslyn = null,
        ChangePolicy? policy = null,
        IConfiguration? configuration = null,
        ILogger? logger = null)
    {
        if (kind is not (EngineeringHarnessKind.ClaudeCli or EngineeringHarnessKind.CodexCli))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "ExternalCliHarness only supports ClaudeCli or CodexCli.");
        }

        _kind = kind;
        _configuration = configuration;
        _logger = logger ?? OsConsoleLog.For<ExternalCliHarness>();
        _sandboxRunner = sandboxRunner ?? ISandboxCommandRunner.CreateDefault(configuration: configuration, logger: _logger);
        _orchestrator = new InoCodeOrchestrator(roslyn, policy, _logger);
    }

    public EngineeringHarnessKind Kind => _kind;

    public string ExecutableName => ResolveExecutable(_kind, _configuration);

    public IReadOnlyList<string> BuildCliArguments(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return _kind switch
        {
            EngineeringHarnessKind.ClaudeCli => ["-p", prompt, "--output-format", "stream-json", "--verbose"],
            EngineeringHarnessKind.CodexCli => ["exec", "--json", prompt],
            _ => [prompt],
        };
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var executable = ExecutableName;
        if (Path.IsPathRooted(executable))
        {
            return Task.FromResult(File.Exists(executable));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] extensions = OperatingSystem.IsWindows()
            ? ["", ".exe", ".cmd", ".bat"]
            : [""];

        foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(rawDir, executable + ext);
                if (File.Exists(candidate))
                {
                    return Task.FromResult(true);
                }
            }
        }

        return Task.FromResult(false);
    }

    public async Task<EngineeringHarnessResult> ExecuteAsync(
        EngineeringHarnessRequest request,
        Func<OperationProgressed, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorktreePath);

        var progressEvents = new List<OperationProgressed>();
        var progressGate = new Lock();
        var sliceId = _kind == EngineeringHarnessKind.ClaudeCli ? "cli-claude" : "cli-codex";

        await InoCodeOrchestrator.EmitProgressAsync(
            request with { Harness = _kind },
            sliceId,
            percent: 0,
            phase: "starting",
            detail: $"Starting {ExecutableName} CLI adapter on '{request.WorktreeId}'",
            progressEvents,
            progressGate,
            onProgress).ConfigureAwait(false);

        var beforeSnapshot = CaptureAllowedFileFingerprints(request.WorktreePath, _orchestrator.Policy);
        var copiedIncludes = CopyWorktreeIncludeFiles(request.SourceRepositoryPath, request.WorktreePath);
        var cleanupResult = await _orchestrator.ExecuteCleanupTargetsAsync(
            request.WorktreePath,
            request.RatifiedPlan?.CleanupTargets,
            ct).ConfigureAwait(false);

        await InoCodeOrchestrator.EmitProgressAsync(
            request with { Harness = _kind },
            sliceId,
            percent: 25,
            phase: "worktree-sync",
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"Synced {copiedIncludes.Length} .worktreeinclude file(s) and cleaned {cleanupResult.DeletedFiles.Length} target(s)"),
            progressEvents,
            progressGate,
            onProgress).ConfigureAwait(false);

        var prompt = BuildPrompt(request);
        var cliArgs = BuildCliArguments(prompt);
        var syntheticLease = new WorkTreeLease(
            RunId: request.RunId,
            RepositoryId: request.RepositoryId,
            SourceRepoPath: request.SourceRepositoryPath ?? request.WorktreePath,
            TreePath: request.WorktreePath,
            RealGitDirPath: request.WorktreePath,
            GitPointerSha256Hex: new string('0', 64),
            BaseBranch: request.BaseBranch ?? "main",
            BaseCommitSha: new string('0', 40),
            BranchName: $"ino/{request.RunId}",
            LeaseId: request.WorktreeId,
            InstanceIndex: request.InstanceIndex,
            Backend: WorkTreeBackendKind.ContainerVolume);

        var cmdResult = await _sandboxRunner.ExecuteAsync(
            syntheticLease,
            new SandboxCommandRequest(
                Executable: ExecutableName,
                Arguments: cliArgs,
                NetworkPolicy: SandboxNetworkPolicy.Bridge,
                Timeout: TimeSpan.FromMinutes(4)),
            ct).ConfigureAwait(false);

        var parsedSummary = ParseStructuredStreamSummary(cmdResult.StdOut, ExecutableName);

        await InoCodeOrchestrator.EmitProgressAsync(
            request with { Harness = _kind },
            sliceId,
            percent: 60,
            phase: "editing",
            detail: parsedSummary,
            progressEvents,
            progressGate,
            onProgress).ConfigureAwait(false);

        if (!cmdResult.Succeeded)
        {
            var errDetail = string.IsNullOrWhiteSpace(cmdResult.StdErr)
                ? $"External CLI '{ExecutableName}' exited with code {cmdResult.ExitCode}."
                : cmdResult.StdErr.Trim();
            return new EngineeringHarnessResult(
                Succeeded: false,
                Harness: _kind,
                Summary: parsedSummary,
                ModelId: $"cli:{ExecutableName}",
                TouchedFiles: cleanupResult.DeletedFiles,
                DeletedFiles: cleanupResult.DeletedFiles,
                CompletedSlices: [],
                ProgressEvents: [.. progressEvents],
                FailureDetail: errDetail);
        }

        var afterSnapshot = CaptureAllowedFileFingerprints(request.WorktreePath, _orchestrator.Policy);
        var touched = DetectChangedFiles(beforeSnapshot, afterSnapshot, cleanupResult.DeletedFiles, cleanupResult.ModifiedFiles);

        var csFiles = touched
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !cleanupResult.DeletedFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var diags = csFiles.Length > 0
            ? await _orchestrator.GetSemanticDiagnosticsAsync(request.WorktreePath, csFiles, ct).ConfigureAwait(false)
            : [];
        var errCount = diags.Count(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));

        await InoCodeOrchestrator.EmitProgressAsync(
            request with { Harness = _kind },
            sliceId,
            percent: 90,
            phase: "diagnostics",
            detail: $"Verified {touched.Length} file(s) from {ExecutableName} ({errCount} semantic error(s))",
            progressEvents,
            progressGate,
            onProgress).ConfigureAwait(false);

        if (errCount > 0)
        {
            var firstErr = diags.First(static d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
            return new EngineeringHarnessResult(
                Succeeded: false,
                Harness: _kind,
                Summary: parsedSummary,
                ModelId: $"cli:{ExecutableName}",
                TouchedFiles: touched,
                DeletedFiles: cleanupResult.DeletedFiles,
                CompletedSlices: [],
                ProgressEvents: [.. progressEvents],
                SemanticErrorCount: errCount,
                FailureDetail: $"Roslyn error {firstErr.Id} in {firstErr.FilePath}:{firstErr.Line}: {firstErr.Message}");
        }

        await InoCodeOrchestrator.EmitProgressAsync(
            request with { Harness = _kind },
            sliceId,
            percent: 100,
            phase: "completed",
            detail: parsedSummary,
            progressEvents,
            progressGate,
            onProgress).ConfigureAwait(false);

        var completedSlice = new EngineerTaskSlice(
            SliceId: sliceId,
            Title: request.Goal,
            OwnedPaths: touched,
            DependsOnSliceIds: [],
            Status: "completed",
            Percent: 100,
            AssignedAgentKey: $"cli.{ExecutableName}");

        return new EngineeringHarnessResult(
            Succeeded: true,
            Harness: _kind,
            Summary: parsedSummary,
            ModelId: $"cli:{ExecutableName}",
            TouchedFiles: touched,
            DeletedFiles: cleanupResult.DeletedFiles,
            CompletedSlices: [completedSlice],
            ProgressEvents: [.. progressEvents],
            SemanticErrorCount: 0);
    }

    internal static string[] CopyWorktreeIncludeFiles(string? sourceRepoPath, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(sourceRepoPath) || !Directory.Exists(sourceRepoPath) || !Directory.Exists(worktreePath))
        {
            return [];
        }

        var srcRoot = Path.GetFullPath(sourceRepoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dstRoot = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(srcRoot, dstRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return [];
        }

        var includeManifest = Path.Combine(srcRoot, ".worktreeinclude");
        if (!File.Exists(includeManifest))
        {
            return [];
        }

        var copied = new List<string>();
        foreach (var rawLine in File.ReadAllLines(includeManifest, Encoding.UTF8))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var clean = trimmed.Replace('\\', '/').Trim('/');
            if (clean.Length == 0
                || Path.IsPathRooted(clean)
                || clean.Split('/').Any(static seg => seg.Length == 0 || seg.StartsWith("..", StringComparison.Ordinal) || string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var srcFile = Path.GetFullPath(Path.Combine(srcRoot, clean.Replace('/', Path.DirectorySeparatorChar)));
            var dstFile = Path.GetFullPath(Path.Combine(dstRoot, clean.Replace('/', Path.DirectorySeparatorChar)));
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!srcFile.StartsWith(srcRoot + Path.DirectorySeparatorChar, cmp)
                || !dstFile.StartsWith(dstRoot + Path.DirectorySeparatorChar, cmp)
                || !File.Exists(srcFile))
            {
                continue;
            }

            var srcInfo = new FileInfo(srcFile);
            if ((srcInfo.Attributes & FileAttributes.ReparsePoint) != 0 || srcInfo.LinkTarget is not null)
            {
                throw new SecurityException($"Rejected symlink/reparse entry '{clean}' in .worktreeinclude.");
            }

            var dstDir = Path.GetDirectoryName(dstFile);
            if (!string.IsNullOrEmpty(dstDir))
            {
                Directory.CreateDirectory(dstDir);
            }

            File.Copy(srcFile, dstFile, overwrite: true);
            copied.Add(clean);
        }

        return [.. copied];
    }

    internal static string ParseStructuredStreamSummary(string stdOut, string executableName)
    {
        if (string.IsNullOrWhiteSpace(stdOut))
        {
            return $"Completed {executableName} execution.";
        }

        string? latestText = null;
        foreach (var rawLine in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!rawLine.StartsWith('{'))
            {
                latestText ??= rawLine;
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(rawLine);
                var root = doc.RootElement;
                if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(res.GetString()))
                {
                    latestText = res.GetString()!;
                }
                else if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(msg.GetString()))
                {
                    latestText = msg.GetString()!;
                }
                else if (root.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(txt.GetString()))
                {
                    latestText = txt.GetString()!;
                }
            }
            catch (JsonException ex)
            {
                OsConsoleLog.For<ExternalCliHarness>().LogDebug(ex, "Non-JSON stdout line from CLI adapter.");
                latestText ??= rawLine;
            }
        }

        var clean = (latestText ?? $"Completed {executableName} execution.").ReplaceLineEndings(" ").Trim();
        return clean.Length <= 280 ? clean : clean[..280].TrimEnd() + "...";
    }

    private static string ResolveExecutable(EngineeringHarnessKind kind, IConfiguration? configuration)
    {
        if (kind == EngineeringHarnessKind.ClaudeCli)
        {
            return configuration?["IntoChatOS:Engineer:ClaudeExecutable"] is { Length: > 0 } customClaude
                ? customClaude.Trim()
                : "claude";
        }

        return configuration?["IntoChatOS:Engineer:CodexExecutable"] is { Length: > 0 } customCodex
            ? customCodex.Trim()
            : "codex";
    }

    private string BuildPrompt(EngineeringHarnessRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Goal: {request.Goal}");
        sb.AppendLine(_orchestrator.Policy.Describe());
        if (request.RatifiedPlan?.Slices is { Length: > 0 } slices)
        {
            sb.AppendLine("Ratified Plan Slices:");
            foreach (var s in slices)
            {
                sb.AppendLine($"- [{s.SliceId}] {s.Title}: {s.Instructions}");
            }
        }

        return sb.ToString().Trim();
    }

    private static Dictionary<string, (long Length, DateTime LastWriteUtc)> CaptureAllowedFileFingerprints(string root, ChangePolicy policy)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var map = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        if (!Directory.Exists(normalizedRoot))
        {
            return map;
        }

        foreach (var fullPath in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(normalizedRoot, fullPath).Replace('\\', '/');
            if (rel.Split('/').Any(static seg => seg.StartsWith('.') || seg is "bin" or "obj" or "artifacts" or "node_modules"))
            {
                continue;
            }

            if (policy.CheckRawPath(rel) is not null)
            {
                continue;
            }

            var info = new FileInfo(fullPath);
            map[rel] = (info.Length, info.LastWriteTimeUtc);
        }

        return map;
    }

    private static string[] DetectChangedFiles(
        Dictionary<string, (long Length, DateTime LastWriteUtc)> before,
        Dictionary<string, (long Length, DateTime LastWriteUtc)> after,
        string[] deletedCleanup,
        string[] modifiedCleanup)
    {
        var changed = new HashSet<string>(deletedCleanup, StringComparer.Ordinal);
        foreach (var mod in modifiedCleanup)
        {
            changed.Add(mod);
        }

        foreach (var (rel, afterMeta) in after)
        {
            if (!before.TryGetValue(rel, out var beforeMeta) || beforeMeta != afterMeta)
            {
                changed.Add(rel);
            }
        }

        foreach (var rel in before.Keys)
        {
            if (!after.ContainsKey(rel))
            {
                changed.Add(rel);
            }
        }

        return [.. changed.Order(StringComparer.Ordinal)];
    }
}
