using System.Text.Json;

namespace IntoChat.OS.Kernel.Tests.Integration;

// One GET /brains/{id}/live server-sent stream read event by event, as the shell's watchLive reads it.
internal sealed class LiveStream : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private LiveStream(HttpResponseMessage response, StreamReader reader) => (_response, _reader) = (response, reader);

    public static async Task<LiveStream> OpenAsync(HttpClient http, string brainId, string type, CancellationToken ct)
    {
        var response = await http.GetAsync($"/brains/{brainId}/live?type={type}", HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return new(response, new StreamReader(await response.Content.ReadAsStreamAsync(ct)));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // The next envelope, the ready marker included.
    public async Task<JsonDocument> NextAsync(CancellationToken ct)
    {
        while (await _reader.ReadLineAsync(ct).AsTask().WaitAsync(Patience, ct) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal)) { return JsonDocument.Parse(line["data: ".Length..]); }
        }
        throw new InvalidOperationException("The live stream ended.");
    }

    // Skips the retained replay up to and including the ready marker.
    public async Task ReadyAsync(CancellationToken ct)
    {
        while (true)
        {
            using var envelope = await NextAsync(ct);
            if (envelope.RootElement.GetProperty("type").GetString() == "ready") { return; }
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
