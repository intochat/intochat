using System.Reflection;
using System.Runtime.Loader;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

// A loaded behavior running its entry point on its own task: exit code 0 on a clean return (or when it
// ends because it was told to stop), 1 on an unhandled exception, whose message is kept for the report.
internal sealed class BehaviorRun : IAsyncDisposable
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stopping = new();
    private readonly RunLog _log;
    private AssemblyLoadContext? _context;

    private BehaviorRun(AssignmentEvent assignment, AssemblyLoadContext context, int logLines)
    {
        Assignment = assignment;
        Id = assignment.Behavior;
        Hash = assignment.Hash;
        _context = context;
        _log = new RunLog(logLines);
        Completion = Task.FromResult(0);
    }

    public AssignmentEvent Assignment { get; }
    public string Id { get; }
    public string Hash { get; }
    public Task<int> Completion { get; private set; }
    public string? Error { get; private set; }
    internal AssemblyLoadContext? Context => _context;

    public string Tail(int lines) => _log.Tail(lines);

    public static BehaviorRun Start(SessionConnection session, AssignmentEvent assignment, AssemblyLoadContext context, Assembly assembly, int logLines, ILogger logger)
    {
        var run = new BehaviorRun(assignment, context, logLines);
        var entry = assembly.EntryPoint ?? throw new InvalidOperationException($"{assignment.Behavior} has no entry point.");
        run.Completion = Task.Run(() => run.ExecuteAsync(session, assignment.Settings, entry, logger));
        return run;
    }

    private async Task<int> ExecuteAsync(SessionConnection session, Dictionary<string, string> settings, MethodInfo entry, ILogger logger)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["Behavior"] = Id, ["Hash"] = Hash });
        using var ambient = AmbientSession.Enter(session, Id, settings, _stopping.Token, Hash, Assignment.Revision);
        using var output = BehaviorConsole.Capture(line => { _log.Append(line); logger.LogInformation("{Behavior}: {Line}", Id, line); });
        try
        {
            object?[] arguments = entry.GetParameters().Length == 0 ? [] : [Array.Empty<string>()];
            switch (entry.Invoke(null, arguments))
            {
                case Task<int> exit: return await exit.ConfigureAwait(false);
                case Task task: await task.ConfigureAwait(false); return 0;
                case int exit: return exit;
                default: return 0;
            }
        }
        catch (Exception error) when (Unwrap(error) is OperationCanceledException && _stopping.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            Error = Unwrap(error).ToString();
            _log.Append(Error);
            logger.LogWarning(Unwrap(error), "Behavior {Behavior} {Hash} failed", Id, Hash);
            return 1;
        }
    }

    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? inner : error;

    public async ValueTask DisposeAsync()
    {
        // A session's disposal and the run's own exit can both dispose it; the first one in does.
        if (Interlocked.Exchange(ref _context, null) is not { } context) { return; }
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(Completion, Task.Delay(StopGrace)).ConfigureAwait(false);
        context.Unload();
        _stopping.Dispose();
    }
}
