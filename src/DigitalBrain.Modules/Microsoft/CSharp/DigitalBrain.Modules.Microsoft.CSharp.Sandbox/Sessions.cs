using System.Collections.Concurrent;
using System.Runtime.Loader;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

// The sessions this process holds: many locally (one container serves every brain), one in a pool
// session (the pool routes by identifier). A start for a session that is already open is a no-op; a
// start with a new session id replaces the old one, whose token no longer speaks for the brain.
internal sealed class Sessions(BehaviorLoader loader, ILoggerFactory loggers)
{
    private readonly ConcurrentDictionary<string, (string Id, Session Session, CancellationTokenSource Lifetime)> _open = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The contracts this image carries, reported to every brain on connect.
    public static string Fingerprint { get; } = ContractsFingerprint.Of(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
        .Where(path => Path.GetFileNameWithoutExtension(path) is "DigitalBrain" or "DigitalBrain.Client"
            || Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.Ordinal))
        .Select(path => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(loaded => loaded.GetName().Name == Path.GetFileNameWithoutExtension(path))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)));

    public async Task StartAsync(string identifier, SessionStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_open.TryGetValue(identifier, out var current) && current.Id == start.Session) { return; }
            await StopCoreAsync(identifier).ConfigureAwait(false);
            var connection = await SessionConnection.OpenAsync(new Uri(start.EdgeUrl), start.Token, Fingerprint, cancellationToken).ConfigureAwait(false);
            var session = new Session(connection, loader, loggers.CreateLogger("Session " + start.Brain));
            var lifetime = new CancellationTokenSource();
            _open[identifier] = (start.Session, session, lifetime);
            _ = session.RunAsync(lifetime.Token);
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

    private async Task<bool> StopCoreAsync(string identifier)
    {
        if (!_open.TryRemove(identifier, out var open)) { return false; }
        await open.Lifetime.CancelAsync().ConfigureAwait(false);
        await open.Session.DisposeAsync().ConfigureAwait(false);
        open.Lifetime.Dispose();
        return true;
    }
}
