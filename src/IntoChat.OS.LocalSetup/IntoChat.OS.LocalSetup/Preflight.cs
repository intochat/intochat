using IntoChat.OS.LocalSetup.Checks;

namespace IntoChat.OS.LocalSetup;

public sealed record PreflightReport(IReadOnlyList<CheckResult> Results)
{
    public bool HasFailure => Results.Any(result => result.Status == CheckStatus.Fail);

    public bool GpuDetected => Results.Any(result => result.Id == "gpu" && result.Status == CheckStatus.Pass);

    public bool OllamaReachable => Results.Any(result => result.Id == "ollama" && result.Status == CheckStatus.Pass);

    public bool ModelMissing => Results.Any(result => result.Id == "model" && result.Status == CheckStatus.Warn);

    public bool FlutterAvailable => Results.Any(result => result.Id == "flutter" && result.Status == CheckStatus.Pass);

    // 0 for pass or warnings, 1 for any hard failure (unless forced to 0).
    public int ExitCode(bool noFail = false) => HasFailure && !noFail ? 1 : 0;
}

public sealed record LocalLaunch(int ExitCode, IReadOnlyList<string> Arguments);

public static class Preflight
{
    public const int UsageExitCode = 2;

    public static IReadOnlyList<IPreflightCheck> Checks { get; } =
    [
        new DockerEngineCheck(), new LinuxContainersCheck(), new DockerMemoryCheck(), new WslCheck(), new DiskCheck(), new PortsCheck(),
        new OllamaCheck(), new ModelPresentCheck(), new GpuCheck(), new DotnetSdkCheck(), new FlutterCheck(),
    ];

    public static async Task<PreflightReport> CollectAsync(IProbeEnvironment environment, PreflightOptions options, CancellationToken cancellationToken)
    {
        var context = new PreflightContext(environment, options);
        var results = new List<CheckResult>();
        foreach (var check in Checks)
        {
            results.AddRange(await check.RunAsync(context, cancellationToken).ConfigureAwait(false));
        }
        return new PreflightReport(results);
    }

    // `preflight [--json] [--no-fail] [--fake <fixture.json>] [--model <tag>] [--endpoint <url>]`; exit 0, 1 or 2 (usage).
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var command = LauncherCommand.Parse(args);
        if (command.Kind is not (LauncherKind.Preflight or LauncherKind.Default) || command.Remaining.Count > 0)
        {
            await output.WriteLineAsync(command.Error ?? "usage: preflight [--json] [--no-fail] [--fake <file>] [--model <tag>]", cancellationToken).ConfigureAwait(false);
            return UsageExitCode;
        }
        var modelError = command.ModelOptions.Validate();
        if (modelError is not null)
        {
            await output.WriteLineAsync(modelError, cancellationToken).ConfigureAwait(false);
            return UsageExitCode;
        }
        if (command.Fake is not null && !File.Exists(command.Fake))
        {
            await output.WriteLineAsync("Fixture file does not exist.", cancellationToken).ConfigureAwait(false);
            return UsageExitCode;
        }

        IProbeEnvironment environment = command.Fake is null ? new SystemProbeEnvironment() : FakeProbeEnvironment.Load(command.Fake);
        var options = new PreflightOptions(command.ModelOptions, ShellEnabled: true, Directory.GetCurrentDirectory());
        var report = await CollectAsync(environment, options, cancellationToken).ConfigureAwait(false);
        var exitCode = report.ExitCode(command.NoFail);
        PreflightRenderer.Render(report, exitCode, command.Json, output);
        var pullExit = await PullIfRequestedAsync(environment, command, report, output, cancellationToken).ConfigureAwait(false);
        return command.NoFail ? exitCode : Math.Max(exitCode, pullExit);
    }

    // `local`: preflight with the model pull enabled, then the AppHost arguments the detected host calls for (GPU only when
    // detected, the shell only when Flutter is available). ExitCode is non-zero when the run must not start the AppHost.
    public static async Task<LocalLaunch> PrepareLocalAsync(LauncherCommand command, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);
        var modelError = command.ModelOptions.Validate();
        if (modelError is not null)
        {
            await output.WriteLineAsync(modelError, cancellationToken).ConfigureAwait(false);
            return new LocalLaunch(UsageExitCode, []);
        }
        if (command.SkipPreflight) { return new LocalLaunch(0, command.LocalAppHostArguments(gpu: false, shellAvailable: true)); }
        IProbeEnvironment environment = command.Fake is null ? new SystemProbeEnvironment() : FakeProbeEnvironment.Load(command.Fake);
        var options = new PreflightOptions(command.ModelOptions, ShellEnabled: true, Directory.GetCurrentDirectory());
        var report = await CollectAsync(environment, options, cancellationToken).ConfigureAwait(false);
        var exitCode = report.ExitCode(command.NoFail);
        PreflightRenderer.Render(report, exitCode, command.Json, output);
        var pullExit = await PullIfRequestedAsync(environment, command with { Pull = true }, report, output, cancellationToken).ConfigureAwait(false);
        exitCode = command.NoFail ? exitCode : Math.Max(exitCode, pullExit);
        return new LocalLaunch(exitCode, exitCode == 0 ? command.LocalAppHostArguments(report.GpuDetected, report.FlutterAvailable) : []);
    }

    // `--pull`: download the configured model when Ollama is up and the tag is missing, printing progress. 0 or 1.
    public static async Task<int> PullIfRequestedAsync(IProbeEnvironment environment, LauncherCommand command, PreflightReport report, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        if (!command.Pull) { return 0; }
        if (!report.OllamaReachable)
        {
            await output.WriteLineAsync("Cannot pull the model: Ollama is not reachable.", cancellationToken).ConfigureAwait(false);
            return 1;
        }
        if (!report.ModelMissing)
        {
            await output.WriteLineAsync("Model is already installed; nothing to pull.", cancellationToken).ConfigureAwait(false);
            return 0;
        }
        var result = await ModelPuller.PullAsync(environment.Http, command.ModelOptions, new ConsolePullProgress(output), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) { await output.WriteLineAsync("Model pull failed: " + result.Error, cancellationToken).ConfigureAwait(false); }
        return result.Succeeded ? 0 : 1;
    }
}
