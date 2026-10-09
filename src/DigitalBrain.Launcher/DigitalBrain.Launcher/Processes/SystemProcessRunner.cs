using System.Diagnostics;

namespace DigitalBrain.Launcher.Processes;

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        using var process = new Process { StartInfo = Info(file, args, workingDirectory, environment) };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new(process.ExitCode, await output, await error);
    }

    public IRunningProcess Start(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var process = new Process { StartInfo = Info(file, args, workingDirectory, environment), EnableRaisingEvents = true };
        process.Start();
        return new Running(process);
    }

    private static ProcessStartInfo Info(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in args) { info.ArgumentList.Add(argument); }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) { info.Environment[name] = value; }
        return info;
    }

    private sealed class Running(Process process) : IRunningProcess
    {
        public int Id => process.Id;
        public Task<int> Exited => process.WaitForExitAsync().ContinueWith(_ => process.ExitCode, TaskScheduler.Default);
        // aspire run owns the AppHost and its DCP children; killing the tree is the documented stop.
        public Task StopAsync(CancellationToken ct) { if (!process.HasExited) { process.Kill(entireProcessTree: true); } return process.WaitForExitAsync(ct); }
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
    }
}
