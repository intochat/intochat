namespace DigitalBrain.Invocation;

/// <summary>
/// Thread-safe, bounded replay-defense cache keyed by <c>(SenderBrainId, MessageId)</c>
/// that rejects duplicate envelopes within the freshness retention window.
/// </summary>
public sealed class BrainEnvelopeReplayCache
{
    /// <summary>Default maximum number of tracked message identifiers.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly int _capacity;
    private readonly TimeSpan _retentionWindow;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Sender, string MessageId), DateTimeOffset> _entries = new();
    private readonly Queue<(string Sender, string MessageId, DateTimeOffset ExpiresAtUtc)> _order = new();

    /// <summary>Creates a bounded replay cache with the given capacity and retention window.</summary>
    public BrainEnvelopeReplayCache(int capacity = DefaultCapacity, TimeSpan? retentionWindow = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Replay cache capacity must be positive.");
        }

        var window = retentionWindow ?? TimeSpan.FromMinutes(10);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionWindow), "Replay cache retention window must be positive.");
        }

        _capacity = capacity;
        _retentionWindow = window;
    }

    /// <summary>Current number of active entries in the cache.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="messageId"/> from <paramref name="sender"/> has already been accepted within the retention window.</summary>
    public bool Contains(BrainId sender, string messageId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            return false;
        }

        var key = (sender.Value ?? string.Empty, messageId);
        lock (_gate)
        {
            EvictExpiredLocked(nowUtc);
            return _entries.TryGetValue(key, out var expiresAt) && expiresAt > nowUtc;
        }
    }

    /// <summary>
    /// Attempts to record <paramref name="messageId"/> from <paramref name="sender"/>.
    /// Returns <see langword="false"/> if the key was already seen within the retention window.
    /// </summary>
    public bool TryAccept(BrainId sender, string messageId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        var key = (sender.Value ?? string.Empty, messageId);
        var expiresAt = nowUtc + _retentionWindow;

        lock (_gate)
        {
            EvictExpiredLocked(nowUtc);

            if (_entries.TryGetValue(key, out var existingExpiry) && existingExpiry > nowUtc)
            {
                return false;
            }

            while (_entries.Count >= _capacity && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                var oldestKey = (oldest.Sender, oldest.MessageId);
                if (_entries.TryGetValue(oldestKey, out var currentExpiry) && currentExpiry == oldest.ExpiresAtUtc)
                {
                    _entries.Remove(oldestKey);
                }
            }

            _entries[key] = expiresAt;
            _order.Enqueue((key.Item1, key.messageId, expiresAt));
            return true;
        }
    }

    private void EvictExpiredLocked(DateTimeOffset nowUtc)
    {
        while (_order.Count > 0 && _order.Peek().ExpiresAtUtc <= nowUtc)
        {
            var oldest = _order.Dequeue();
            var oldestKey = (oldest.Sender, oldest.MessageId);
            if (_entries.TryGetValue(oldestKey, out var currentExpiry) && currentExpiry == oldest.ExpiresAtUtc)
            {
                _entries.Remove(oldestKey);
            }
        }
    }
}
