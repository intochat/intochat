namespace IntoChat.Cloud.Lobby;

internal sealed class GuestTrySessionStore(GuestTryOptions options)
{
    private readonly Dictionary<string, GuestTrySession> _activeSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _ipIssuanceHistory = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int ActiveSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _activeSessions.Count;
            }
        }
    }

    public TResult WithLock<TResult>(Func<GuestTrySessionRegistry, TResult> action)
    {
        lock (_gate)
        {
            return action(new GuestTrySessionRegistry(this));
        }
    }

    internal readonly struct GuestTrySessionRegistry(GuestTrySessionStore store)
    {
        public bool TryGetSession(string sessionId, out GuestTrySession? session)
            => store._activeSessions.TryGetValue(sessionId, out session);

        public void SaveSession(GuestTrySession session)
            => store._activeSessions[session.SessionId] = session;

        public bool RemoveSession(string sessionId)
            => store._activeSessions.Remove(sessionId);

        public int ActiveCount => store._activeSessions.Count;

        public List<DateTimeOffset> GetOrAddIpHistory(string callerPrefix)
        {
            if (!store._ipIssuanceHistory.TryGetValue(callerPrefix, out var issuedTimestamps))
            {
                issuedTimestamps = [];
                store._ipIssuanceHistory[callerPrefix] = issuedTimestamps;
            }
            return issuedTimestamps;
        }

        public int SweepExpired(DateTimeOffset now)
            => store.SweepExpiredLocked(now);
    }

    private int SweepExpiredLocked(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (id, session) in _activeSessions.ToArray())
        {
            if (session.ExpiresAt <= now)
            {
                _activeSessions.Remove(id);
                removed++;
            }
        }

        var window = options.RateLimitWindow;
        foreach (var (prefix, timestamps) in _ipIssuanceHistory.ToArray())
        {
            timestamps.RemoveAll(ts => now - ts >= window);
            if (timestamps.Count == 0)
            {
                _ipIssuanceHistory.Remove(prefix);
            }
        }

        return removed;
    }
}
