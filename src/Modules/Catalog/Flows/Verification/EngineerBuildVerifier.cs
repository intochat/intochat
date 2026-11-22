using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record TrxParseResult(
    int Passed,
    int Failed,
    int Skipped,
    string[] Diagnostics);

internal sealed partial class EngineerBuildVerifier(
    IConfiguration? configuration = null,
    ISandboxCommandRunner? sandboxRunner = null,
    ILogger<EngineerBuildVerifier>? logger = null) : IEngineerBuildVerifier
{
    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerBuildVerifier>();
    private readonly ISandboxCommandRunner _sandboxRunner = sandboxRunner
        ?? ISandboxCommandRunner.CreateDefault(
            configuration: configuration,
            logger: (ILogger?)logger ?? OsConsoleLog.For<EngineerBuildVerifier>());

    public Task<EngineerBuildReport> VerifyAsync(
        string worktreePath,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
        => VerifyCoreAsync(worktreePath, lease: null, changedFiles, ct);

    public Task<EngineerBuildReport> VerifyAsync(
        WorkTreeLease lease,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return VerifyCoreAsync(lease.WorkspacePath, lease, changedFiles, ct);
    }

    private async Task<EngineerBuildReport> VerifyCoreAsync(
        string worktreePath,
        WorkTreeLease? lease,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var sw = Stopwatch.StartNew();
        using var buildActivity = EngineerTelemetry.Source.StartActivity("engineer.build");
        buildActivity?.SetTag("engineer.files", changedFiles.Count);
        var root = Path.GetFullPath(worktreePath);
        var diagnostics = new List<string>();
        var warnings = 0;
        var errors = 0;
        var useContainerVolumeSandbox = lease?.Backend == WorkTreeBackendKind.ContainerVolume;

        var buildProjects = DiscoverAffectedProjects(root, changedFiles);
        var dotnetBuildPassed = true;
        foreach (var csproj in buildProjects)
        {
            var projectTarget = useContainerVolumeSandbox ? ToRelativeUnixPath(root, csproj) : csproj;
            var res = await ExecuteVerificationToolAsync(
                lease,
                "dotnet",
                ["build", projectTarget, "-c", "Release", "--nologo", "--disable-build-servers", "-v", "minimal"],
                root,
                relativeWorkingDir: null,
                ct).ConfigureAwait(false);
            var combined = res.StdOut + "\n" + res.StdErr;
            warnings += BuildWarningRegex().Matches(combined).Count;
            errors += BuildErrorRegex().Matches(combined).Count;
            CollectDiagnosticLines(combined, diagnostics);
            if (res.ExitCode != 0)
            {
                dotnetBuildPassed = false;
                if (errors == 0) { errors++; }
            }
        }

        var flutterAnalyzePassed = true;
        var hasChangedDart = changedFiles.Any(static f => f.EndsWith(".dart", StringComparison.OrdinalIgnoreCase));
        var flutterDir = ResolveFlutterRoot(root);
        if (hasChangedDart && flutterDir is not null)
        {
            var relFlutterDir = string.Equals(
                Path.GetFullPath(flutterDir),
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? null
                : ToRelativeUnixPath(root, flutterDir);
            var analyze = await ExecuteVerificationToolAsync(
                lease,
                "flutter",
                ["analyze", "--no-pub"],
                flutterDir,
                relFlutterDir,
                ct).ConfigureAwait(false);
            var combined = analyze.StdOut + "\n" + analyze.StdErr;
            CollectDiagnosticLines(combined, diagnostics);
            if (analyze.ExitCode != 0)
            {
                flutterAnalyzePassed = false;
                errors++;
            }
        }

        var dotnetTestPassed = true;
        var passedTests = 0;
        var failedTests = 0;
        var testProjects = DiscoverTestProjects(root);
        if (dotnetBuildPassed && testProjects.Length > 0 && IsLiveCliVerificationEnabled(configuration))
        {
            foreach (var testProj in testProjects)
            {
                var testTarget = useContainerVolumeSandbox ? ToRelativeUnixPath(root, testProj) : testProj;
                var trxDir = useContainerVolumeSandbox
                    ? Path.Combine(root, ".intochat-trx-" + Guid.NewGuid().ToString("N")[..8])
                    : Path.Combine(Path.GetTempPath(), "intochat-trx-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(trxDir);
                var trxDirArg = useContainerVolumeSandbox ? ToRelativeUnixPath(root, trxDir) : trxDir;

                try
                {
                    var isMtp = UsesMicrosoftTestingPlatform(root, testProj, _logger);
                    string[] testArgs = isMtp
                        ?
                        [
                            "test", "--project", testTarget, "-c", "Release",
                            "--results-directory", trxDirArg,
                            "--report-xunit-trx", "--report-xunit-trx-filename", "results.trx",
                        ]
                        :
                        [
                            "test", testTarget, "-c", "Release", "--nologo", "--disable-build-servers", "-v", "minimal",
                            "--logger", "trx;LogFileName=results.trx",
                            "--results-directory", trxDirArg,
                        ];

                    var testRes = await ExecuteVerificationToolAsync(
                        lease,
                        "dotnet",
                        testArgs,
                        root,
                        relativeWorkingDir: null,
                        ct).ConfigureAwait(false);
                    var combined = testRes.StdOut + "\n" + testRes.StdErr;
                    CollectDiagnosticLines(combined, diagnostics);

                    var trxFile = FindTrxFile(trxDir);
                    int p;
                    int f;
                    if (trxFile is not null)
                    {
                        var trx = ParseTrxFile(trxFile);
                        p = trx.Passed;
                        f = trx.Failed;
                        diagnostics.AddRange(trx.Diagnostics);
                    }
                    else
                    {
                        (p, f) = ParseTestCounts(combined);
                    }

                    passedTests += p;
                    failedTests += f;
                    if (testRes.ExitCode != 0 || f > 0)
                    {
                        dotnetTestPassed = false;
                        errors += Math.Max(1, f);
                    }
                }
                finally
                {
                    TryDeleteDirectory(trxDir, _logger);
                }
            }
        }

        sw.Stop();
        var succeeded = dotnetBuildPassed && flutterAnalyzePassed && dotnetTestPassed;
        var summary = buildProjects.Length == 0 && !hasChangedDart
            ? $"Verified {changedFiles.Count} file(s) in {sw.ElapsedMilliseconds} ms (no .csproj/.dart target requires compilation)."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Build={dotnetBuildPassed} ({buildProjects.Length} csproj), Analyze={flutterAnalyzePassed}, Tests={dotnetTestPassed} (passed={passedTests}, failed={failedTests}, warnings={warnings}, errors={errors}) in {sw.ElapsedMilliseconds} ms.");

        return new EngineerBuildReport(
            Succeeded: succeeded,
            DotnetBuildPassed: dotnetBuildPassed,
            FlutterAnalyzePassed: flutterAnalyzePassed,
            DotnetTestPassed: dotnetTestPassed,
            PassedTests: passedTests,
            FailedTests: failedTests,
            WarningCount: warnings,
            ErrorCount: errors,
            Diagnostics: [.. diagnostics.Take(50)],
            DurationMs: sw.ElapsedMilliseconds,
            Summary: summary);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> ExecuteVerificationToolAsync(
        WorkTreeLease? lease,
        string executable,
        IReadOnlyList<string> args,
        string hostWorkingDir,
        string? relativeWorkingDir,
        CancellationToken ct)
    {
        if (lease is { Backend: WorkTreeBackendKind.ContainerVolume })
        {
            var sandboxResult = await _sandboxRunner.ExecuteAsync(
                lease,
                new SandboxCommandRequest(
                    executable,
                    args,
                    RelativeWorkingDirectory: relativeWorkingDir,
                    Timeout: TimeSpan.FromMinutes(3)),
                ct).ConfigureAwait(false);
            return (sandboxResult.ExitCode, sandboxResult.StdOut, sandboxResult.StdErr);
        }

        return await RunToolAsync(executable, args, hostWorkingDir, ct).ConfigureAwait(false);
    }

    private static string ToRelativeUnixPath(string root, string targetPath)
        => Path.GetRelativePath(root, targetPath).Replace('\\', '/');

    private static bool IsLiveCliVerificationEnabled(IConfiguration? cfg)
        => !string.Equals(cfg?["IntoChatOS:Engineer:RunTestSuites"], "false", StringComparison.OrdinalIgnoreCase);

    private static string[] DiscoverAffectedProjects(string root, IReadOnlyList<string> changedFiles)
    {
        var projects = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var rel in changedFiles.Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && dir.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                var found = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csproj") : [];
                if (found.Length > 0)
                {
                    projects.Add(found[0]);
                    break;
                }

                if (string.Equals(dir, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        return [.. projects.Order(StringComparer.Ordinal)];
    }

    private static string[] DiscoverTestProjects(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(root, "*.Tests*.csproj", SearchOption.AllDirectories)
                .Where(static p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Take(2),
        ];
    }

    private static string? ResolveFlutterRoot(string root)
    {
        var uiPubspec = Path.Combine(root, "ui", "pubspec.yaml");
        if (File.Exists(uiPubspec))
        {
            return Path.Combine(root, "ui");
        }

        var rootPubspec = Path.Combine(root, "pubspec.yaml");
        return File.Exists(rootPubspec) ? root : null;
    }

    private static void CollectDiagnosticLines(string output, List<string> sink)
    {
        foreach (var raw in output.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains(": warning ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("error •", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(trimmed);
            }
        }
    }

    internal static TrxParseResult ParseTrxFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var xml = File.ReadAllText(path, Encoding.UTF8);
        return ParseTrxXml(xml);
    }

    internal static TrxParseResult ParseTrxXml(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var doc = XDocument.Parse(xml, LoadOptions.None);

        var unitResults = doc
            .Descendants()
            .Where(static e => string.Equals(e.Name.LocalName, "UnitTestResult", StringComparison.Ordinal))
            .ToArray();

        var failureDiagnostics = new List<string>();
        var countedPassed = 0;
        var countedFailed = 0;
        var countedSkipped = 0;

        foreach (var result in unitResults)
        {
            var outcome = result.Attribute("outcome")?.Value ?? string.Empty;
            if (string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase))
            {
                countedPassed++;
            }
            else if (string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outcome, "Skipped", StringComparison.OrdinalIgnoreCase))
            {
                countedSkipped++;
            }
            else if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outcome, "Error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outcome, "Aborted", StringComparison.OrdinalIgnoreCase))
            {
                countedFailed++;
                var testName = result.Attribute("testName")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(testName))
                {
                    testName = "(unnamed test)";
                }

                var errorInfo = result
                    .Descendants()
                    .FirstOrDefault(static e => string.Equals(e.Name.LocalName, "ErrorInfo", StringComparison.Ordinal));
                var message = errorInfo?
                    .Elements()
                    .FirstOrDefault(static e => string.Equals(e.Name.LocalName, "Message", StringComparison.Ordinal))
                    ?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = errorInfo?
                        .Elements()
                        .FirstOrDefault(static e => string.Equals(e.Name.LocalName, "StackTrace", StringComparison.Ordinal))
                        ?.Value?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault();
                }

                var compactMessage = string.IsNullOrWhiteSpace(message)
                    ? string.Empty
                    : string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

                failureDiagnostics.Add(string.IsNullOrWhiteSpace(compactMessage)
                    ? $"Failed test '{testName}'"
                    : $"Failed test '{testName}': {compactMessage}");
            }
        }

        var counters = doc
            .Descendants()
            .FirstOrDefault(static e => string.Equals(e.Name.LocalName, "Counters", StringComparison.Ordinal));

        if (counters is not null)
        {
            var passed = ParseXmlInt(counters, "passed");
            var failed = ParseXmlInt(counters, "failed")
                + ParseXmlInt(counters, "error")
                + ParseXmlInt(counters, "timeout")
                + ParseXmlInt(counters, "aborted");
            var notExecuted = ParseXmlInt(counters, "notExecuted");
            var skipped = notExecuted > 0 ? notExecuted : countedSkipped;
            return new TrxParseResult(passed, failed, skipped, [.. failureDiagnostics]);
        }

        return new TrxParseResult(countedPassed, countedFailed, countedSkipped, [.. failureDiagnostics]);
    }

    private static int ParseXmlInt(XElement element, string attributeName)
        => int.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static string? FindTrxFile(string trxDir)
    {
        if (!Directory.Exists(trxDir))
        {
            return null;
        }

        var direct = Path.Combine(trxDir, "results.trx");
        if (File.Exists(direct))
        {
            return direct;
        }

        return Directory.EnumerateFiles(trxDir, "*.trx", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static bool UsesMicrosoftTestingPlatform(string root, string testProjectPath, ILogger logger)
    {
        try
        {
            var globalJson = Path.Combine(root, "global.json");
            if (File.Exists(globalJson)
                && File.ReadAllText(globalJson, Encoding.UTF8).Contains("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var propsFile = Path.Combine(root, "Directory.Build.props");
            if (File.Exists(propsFile))
            {
                var props = File.ReadAllText(propsFile, Encoding.UTF8);
                if (props.Contains("UseMicrosoftTestingPlatformRunner", StringComparison.OrdinalIgnoreCase)
                    || props.Contains("xunit.v3.mtp", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (File.Exists(testProjectPath))
            {
                var proj = File.ReadAllText(testProjectPath, Encoding.UTF8);
                if (proj.Contains("UseMicrosoftTestingPlatformRunner", StringComparison.OrdinalIgnoreCase)
                    || proj.Contains("xunit.v3.mtp", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (IOException ex)
        {
            logger.LogSwallowed(ex, "Failed to inspect project metadata for Microsoft.Testing.Platform detection; falling back to VSTest flags.");
        }

        return false;
    }

    private static void TryDeleteDirectory(string path, ILogger logger)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogSwallowed(ex, "Best-effort cleanup of temporary TRX results directory.");
        }
    }

    private static (int Passed, int Failed) ParseTestCounts(string output)
    {
        var m = TestSummaryRegex().Match(output);
        if (m.Success)
        {
            var failed = int.TryParse(m.Groups["failed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
            var passed = int.TryParse(m.Groups["passed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
            return (passed, failed);
        }

        var mtp = MtpTestSummaryRegex().Match(output);
        if (mtp.Success)
        {
            var failed = int.TryParse(mtp.Groups["failed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
            var passed = int.TryParse(mtp.Groups["passed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
            return (passed, failed);
        }

        return (0, 0);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunToolAsync(
        string executable,
        IReadOnlyList<string> args,
        string workingDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        ChildEnvironmentPolicy.Scrub(psi);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start verification process '{executable}'.");
        proc.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception killEx) when (killEx is not OperationCanceledException)
            {
                _logger.LogSwallowed(killEx, "Best-effort process termination during build/test cancellation.");
            }

            throw;
        }
    }

    [GeneratedRegex(@":\s*warning\s+[A-Z]+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildWarningRegex();

    [GeneratedRegex(@":\s*error\s+[A-Z]+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildErrorRegex();

    [GeneratedRegex(@"Failed:\s*(?<failed>\d+).*?Passed:\s*(?<passed>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TestSummaryRegex();

    [GeneratedRegex(@"failed:\s*(?<failed>\d+).*?succeeded:\s*(?<passed>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex MtpTestSummaryRegex();
}

