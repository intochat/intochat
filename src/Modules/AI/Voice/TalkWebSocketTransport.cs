using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.AI.Voice;

// Bounded WebSocket frame I/O, serialized TalkJson event/binary sends, and safe close handling.
internal sealed class TalkWebSocketTransport(WebSocket ws) : IDisposable
{
    internal const int MaxBinaryFrameBytes = 1024 * 1024;
    internal const int MaxTextFrameBytes = 64 * 1024;
    internal const int MaxCloseReasonBytes = 123;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsOpen => ws.State == WebSocketState.Open;

    public async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveFrameAsync(byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) { return (WebSocketMessageType.Close, []); }

            var limit = result.MessageType == WebSocketMessageType.Binary ? MaxBinaryFrameBytes : MaxTextFrameBytes;
            if (ms.Length + result.Count > limit)
            {
                throw new InvalidDataException($"WebSocket frame exceeded the {limit}-byte limit.");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) { return (result.MessageType, ms.ToArray()); }
        }
    }

    public Task SendEventAsync(TalkEvent ev, CancellationToken ct)
        => ws.State != WebSocketState.Open
            ? Task.CompletedTask
            : SendLockedAsync(JsonSerializer.SerializeToUtf8Bytes(ev, TalkJson.Default.TalkEvent), WebSocketMessageType.Text, ct);

    public Task SendBinaryAsync(byte[] pcm, CancellationToken ct)
        => ws.State != WebSocketState.Open
            ? Task.CompletedTask
            : SendLockedAsync(pcm, WebSocketMessageType.Binary, ct);

    public async Task CloseSafelyAsync(WebSocketCloseStatus status, string description)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) { return; }

        try
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(status, TruncateUtf8(description, MaxCloseReasonBytes), closeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            OsConsoleLog.For<TalkEvent>().LogSwallowed(error, "closing talk WebSocket");
        }
    }

    public void Dispose()
    {
        _sendLock.Dispose();
        ws.Dispose();
    }

    private async Task SendLockedAsync(byte[] payload, WebSocketMessageType messageType, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(payload, messageType, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal static string TruncateUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text)) { return string.Empty; }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes) { return text; }

        var length = maxBytes;
        while (length > 0 && (bytes[length - 1] & 0b1100_0000) == 0b1000_0000) { length--; }
        if (length > 0 && (bytes[length - 1] & 0b1000_0000) != 0) { length--; }
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
