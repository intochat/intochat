using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using DigitalBrain.Access;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.AI.Voice;

// Short-lived, single-use opaque ticket store for browser WebSocket upgrades to /ai/talk.
// Zero bearer tokens in query strings: each ticket is 256 bits of CSPRNG entropy, bound to a
// specific Caller principal and ConversationId, valid for 30 seconds, and removed on first redemption.
internal sealed class TalkTicketStore(TimeProvider? clock = null)
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);
    private const int MaxPendingTickets = 4_096;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    private sealed record Entry(Caller Caller, ConversationId Conversation, DateTimeOffset ExpiresAt);

    public (string Ticket, int ExpiresInSeconds) Issue(Caller caller, ConversationId conversation)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var now = _clock.GetUtcNow();
        PruneExpired(now);

        if (_tickets.Count >= MaxPendingTickets)
        {
            throw new InvalidOperationException("Too many pending voice tickets; try again shortly.");
        }

        var ticket = CreateOpaqueToken();
        _tickets[ticket] = new Entry(caller, conversation, now + DefaultTtl);
        return (ticket, (int)DefaultTtl.TotalSeconds);
    }

    public bool TryRedeem(
        string? ticket,
        ConversationId conversation,
        [NotNullWhen(true)] out Caller? caller)
    {
        caller = null;
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return false;
        }

        // Single-use: always remove atomically on the first attempt, even if expired or mismatched.
        if (!_tickets.TryRemove(ticket.Trim(), out var entry))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        if (entry.ExpiresAt <= now)
        {
            return false;
        }

        if (entry.Conversation != conversation)
        {
            return false;
        }

        caller = entry.Caller;
        return true;
    }

    private static string CreateOpaqueToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _tickets)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _tickets.TryRemove(pair.Key, out _);
            }
        }
    }
}
