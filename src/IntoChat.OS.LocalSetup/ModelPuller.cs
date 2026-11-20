using System.Text;
using System.Text.Json;

namespace IntoChat.OS.LocalSetup;

public sealed record PullProgress(string Status, string? Digest, long? Total, long? Completed)
{
    // "pulling a1b2c3d4 1.2/3.4 GB 35%" for a layer download; the bare status for manifest, verify and success lines.
    public string Render()
    {
        if (Digest is null || Total is not > 0 || Completed is null) { return Status; }
        var id = Digest.StartsWith("sha256:", StringComparison.Ordinal) ? Digest["sha256:".Length..] : Digest;
        var percent = (int)(Completed.Value * 100 / Total.Value);
        return $"pulling {id[..Math.Min(8, id.Length)]} {SizeText.Pair(Completed.Value, Total.Value)} {percent}%";
    }
}

public sealed record PullResult(bool Succeeded, string? Error);

// Streams POST /api/pull as NDJSON. Cancelling leaves the partial download on disk; Ollama resumes it on the next pull.
public static class ModelPuller
{
    public static async Task<PullResult> PullAsync(HttpMessageHandler handler, LocalModelOptions options, IProgress<PullProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        var invalid = options.Validate();
        if (invalid is not null) { return new PullResult(false, invalid); }

        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.EndpointUri, "/api/pull"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = options.Model, stream = true }), Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return new PullResult(false, $"Ollama answered {(int)response.StatusCode} to the pull request."); }
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error)) { return new PullResult(false, error.GetString() ?? "Ollama reported an error."); }
                var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? string.Empty : string.Empty;
                progress.Report(new PullProgress(status, Text(root, "digest"), Number(root, "total"), Number(root, "completed")));
                if (status == "success") { return new PullResult(true, null); }
            }
            return new PullResult(false, "The pull stream ended before Ollama reported success; run again to resume.");
        }
        catch (HttpRequestException exception)
        {
            return new PullResult(false, "Could not reach Ollama: " + exception.Message);
        }
        catch (JsonException)
        {
            return new PullResult(false, "Ollama sent a malformed progress line.");
        }
    }

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static long? Number(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
}

// Prints a pull progress line only when its text changes, so a fast stream does not flood the console.
public sealed class ConsolePullProgress(TextWriter output) : IProgress<PullProgress>
{
    private string? _last;

    public void Report(PullProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Render();
        if (text == _last) { return; }
        _last = text;
        output.WriteLine(text);
    }
}
