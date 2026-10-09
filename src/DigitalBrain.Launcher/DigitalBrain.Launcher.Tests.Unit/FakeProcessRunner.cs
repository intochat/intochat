using DigitalBrain.Launcher.Processes;

namespace DigitalBrain.Launcher.Tests.Unit;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<string> Calls { get; } = [];
    public List<FakeRunningProcess> Started { get; } = [];
    public Func<string, string[], ProcessResult> Answer { get; set; } = (_, _) => new(0, "", "");

    public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        Calls.Add(file + " " + string.Join(' ', args) + " @" + workingDirectory);
        // Real git worktree add creates the checkout directory; the fake does the same so
        // ProposeAsync can move the staging worktree into its revision path.
        if (args is ["worktree", "add", "--detach", var path, "HEAD"]) { Directory.CreateDirectory(path); }
        return Task.FromResult(Answer(file, args));
    }

    public IRunningProcess Start(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        Calls.Add(file + " " + string.Join(' ', args) + " @" + workingDirectory);
        var process = new FakeRunningProcess(Started.Count + 1, environment);
        Started.Add(process);
        return process;
    }
}

internal sealed class FakeRunningProcess(int id, IReadOnlyDictionary<string, string> environment) : IRunningProcess
{
    private readonly TaskCompletionSource<int> _exit = new();
    public int Id => id;
    public IReadOnlyDictionary<string, string> Environment => environment;
    public bool Stopped { get; private set; }
    public Task<int> Exited => _exit.Task;
    public Task StopAsync(CancellationToken ct) { Stopped = true; _exit.TrySetResult(0); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
