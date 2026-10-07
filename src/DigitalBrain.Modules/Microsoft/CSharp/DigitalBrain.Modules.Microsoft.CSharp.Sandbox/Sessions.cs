using System.Collections.Concurrent;
using System.Runtime.Loader;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

// The sessions this process holds: many locally (one container serves every brain), one in a pool
// session (the pool routes by identifier). A start is accepted at once and the session dials the brain
// on its own: the brain starts sessions from inside a grain turn, and answering only after dialing back
// would wait on that very turn. A start for the session already held is a no-op; a start with a new
// session id replaces the old one, whose token no longer speaks for the brain. A session that cannot
// connect is forgotten, so the brain's next check finds it gone and starts it again.
internal sealed class Sessions(BehaviorLoader loader, ILoggerFactory loggers, HttpMessageHandler? edge = null)
{
    private readonly ConcurrentDictionary<string, Open> _open = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = loggers.CreateLogger<Sessions>();

    // The contracts this image carries, reported to every brain on connect.
    public static string Fingerprint { get; } = ContractsFingerprint.Of(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
        .Where(path => Path.GetFileNameWithoutExtension(path) is "DigitalBrain" or "DigitalBrain.Client"
            || Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.Ordinal))
        .Select(path => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(loaded => loaded.GetName().Name == Path.GetFileNameWithoutExtension(path))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)));

    public async Task StartAsync(string identifier, SessionStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        var edgeUrl = new Uri(start.EdgeUrl);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_open.TryGetValue(identifier, out var current) && current.Id == start.Session) { return; }
            await StopCoreAsync(identifier).ConfigureAwait(false);
            var open = new Open(start.Session);
            _open[identifier] = open;
            open.Running = RunAsync(identifier, open, start, edgeUrl);
        }
        finally { _gate.Release(); }
    }

    public bool Contains(string identifier) => _open.ContainsKey(identifier);

    public async Task<bool> StopAsync(string identifier)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await StopCoreAsync(identifier).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(string identifier, Open open, SessionStart start, Uri edgeUrl)
    {
        await Task.Yield();
        try
        {
            var connection = await SessionConnection.OpenAsync(edgeUrl, start.Token, Fingerprint, edge, open.Lifetime.Token).ConfigureAwait(false);
            open.Session = new Session(connection, loader, loggers.CreateLogger("Session " + start.Brain));
            _logger.LogInformation("Session {Session} of brain {Brain} connected", start.Session, start.Brain);
            await open.Session.RunAsync(open.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (open.Lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogError(error, "Session {Session} of brain {Brain} could not connect; the brain starts it again", start.Session, start.Brain);
            _open.TryRemove(new(identifier, open));
        }
    }

    private async Task<bool> StopCoreAsync(string identifier)
    {
        if (!_open.TryRemove(identifier, out var open)) { return false; }
        await open.Lifetime.CancelAsync().ConfigureAwait(false);
        await open.Running.ConfigureAwait(false);
        if (open.Session is { } session) { await session.DisposeAsync().ConfigureAwait(false); }
        open.Lifetime.Dispose();
        return true;
    }

    private sealed class Open(string id)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Lifetime { get; } = new();
        public Task Running { get; set; } = Task.CompletedTask;
        public Session? Session { get; set; }
    }
}
