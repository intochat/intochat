using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DigitalBrain.Microsoft.CSharp.Sessions;

// The open stream of each connected session on this silo, so a brain runtime can push events to its
// host. Not grain state: a session reconnects to whichever silo serves it and is resent what is pending.
internal sealed class SessionStreams
{
    private readonly ConcurrentDictionary<string, Channel<(string Kind, object Payload)>> _open = new(StringComparer.Ordinal);

    public Channel<(string Kind, object Payload)> Open(string session)
    {
        var channel = Channel.CreateUnbounded<(string, object)>(new() { SingleReader = true });
        if (_open.TryGetValue(session, out var previous)) { previous.Writer.TryComplete(); }
        _open[session] = channel;
        return channel;
    }

    public bool TryPush(string session, string kind, object payload)
        => _open.TryGetValue(session, out var channel) && channel.Writer.TryWrite((kind, payload));

    public void Close(string session, Channel<(string Kind, object Payload)> channel)
    {
        if (_open.TryRemove(new(session, channel))) { channel.Writer.TryComplete(); }
    }
}
