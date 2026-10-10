using System.Diagnostics;

namespace DigitalBrain.Microsoft.Aspire.Candidates;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal interface IRunningProcess : IAsyncDisposable
{
    int Id { get; }
    Task<int> Exited { get; }
    Task StopAsync(CancellationToken ct);
}

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct);
    IRunningProcess Start(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        using var process = new Process { StartInfo = Info(file, args, workingDirectory, environment, redirect: true) };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            throw;
        }
        return new(process.ExitCode, await output, await error);
    }

    // A long-running process inherits the host's streams: nobody drains a redirected pipe, and a full one blocks the child.
    public IRunningProcess Start(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var process = new Process { StartInfo = Info(file, args, workingDirectory, environment, redirect: false), EnableRaisingEvents = true };
        process.Start();
        return new Running(process);
    }

    private static ProcessStartInfo Info(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, bool redirect)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = redirect, RedirectStandardError = redirect, UseShellExecute = false };
        foreach (var argument in args) { info.ArgumentList.Add(argument); }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) { info.Environment[name] = value; }
        return info;
    }

    private sealed class Running(Process process) : IRunningProcess
    {
        public int Id => process.Id;
        public Task<int> Exited => process.WaitForExitAsync().ContinueWith(_ => process.ExitCode, TaskScheduler.Default);
        public Task StopAsync(CancellationToken ct) { if (!process.HasExited) { process.Kill(entireProcessTree: true); } return process.WaitForExitAsync(ct); }
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
    }
}
