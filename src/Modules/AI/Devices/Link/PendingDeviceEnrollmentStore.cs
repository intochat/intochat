namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Thread-safe in-memory store for anti-drive-by <c>intochat://enroll</c> protocol handoffs.
/// Single-use enrollment codes are held strictly in memory with a 10-minute TTL and are never
/// persisted to disk or written to logs.
/// </summary>
internal sealed class PendingDeviceEnrollmentStore(TimeProvider? clock = null) : IPendingDeviceEnrollmentStore
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private PendingEntry? _current;

    public PendingDeviceEnrollmentInfo? Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is null)
                {
                    return null;
                }

                if (_clock.GetUtcNow() >= _current.ExpiresAt)
                {
                    _current = null;
                    return null;
                }

                return _current.ToInfo();
            }
        }
    }

    public PendingDeviceEnrollmentInfo Stage(Uri serverOrigin, Code code)
    {
        ArgumentNullException.ThrowIfNull(serverOrigin);
        var now = _clock.GetUtcNow();
        var entry = new PendingEntry(serverOrigin, code, now, now + DefaultTtl);
        lock (_gate)
        {
            _current = entry;
            return entry.ToInfo();
        }
    }

    public bool TryConsume(out Uri? serverOrigin, out Code code)
    {
        lock (_gate)
        {
            var entry = _current;
            _current = null;
            if (entry is null || _clock.GetUtcNow() >= entry.ExpiresAt)
            {
                serverOrigin = null;
                code = default;
                return false;
            }

            serverOrigin = entry.ServerOrigin;
            code = entry.Code;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    private sealed record PendingEntry(Uri ServerOrigin, Code Code, DateTimeOffset ReceivedAt, DateTimeOffset ExpiresAt)
    {
        public PendingDeviceEnrollmentInfo ToInfo() => new(ServerOrigin, ReceivedAt, ExpiresAt);
    }
}
