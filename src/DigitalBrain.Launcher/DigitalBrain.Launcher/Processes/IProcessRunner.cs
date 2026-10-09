namespace DigitalBrain.Launcher.Processes;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public interface IRunningProcess : IAsyncDisposable
{
    int Id { get; }
    Task<int> Exited { get; }
    Task StopAsync(CancellationToken ct);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct);
    IRunningProcess Start(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment);
}
