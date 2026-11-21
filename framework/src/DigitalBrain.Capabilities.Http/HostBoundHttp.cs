using System.Net;
using System.Text;
using System.Text.Json;

namespace DigitalBrain.Capabilities.Http;

public sealed record FetchedText(Uri FinalUrl, string? MediaType, string Text);

// Vetted HTTP GET: checks every request and redirect hop (at most five; the client never follows one
// itself), 30 seconds, a body of at most 1 MiB. For allow-listed calls a literal IP passes only when the
// allow-list names that IP itself; the handler connects only to a public address the host resolves to (VettedConnect).
public sealed class HostBoundHttp(HttpClient client)
{
    public const int MaxRedirects = 5;
    public const int MaxBytes = 1 << 20;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private long _totalRequests;
    private long _totalBytesRead;

    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    public long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);

    internal static HttpClient Shared { get; } = new(Handler(VettedConnect.Dns)) { Timeout = Timeout };

    // No redirect followed by the client, no proxy (the vetted address is the one connected to), and every connection vetted.
    public static SocketsHttpHandler Handler(VettedConnect.Resolver resolve)
        => new() { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = new VettedConnect(resolve).Connect };

    public async Task<JsonElement> GetJson(Uri url, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        var text = await GetText(url, allowed, ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        // Named by the host the caller asked for, never a host a redirect led to.
        catch (JsonException error) { throw new HttpRequestException($"{url.Host} did not return JSON", error); }
    }

    public async Task<string> GetText(Uri url, IReadOnlyList<string> allowed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(allowed);
        var fetched = await Fetch(url, hop =>
        {
            RequireNamedIfLiteral(hop, allowed);
            HttpHosts.Require(hop, allowed);
        }, ct).ConfigureAwait(false);
        return fetched.Text;
    }

    public Task<FetchedText> GetPublic(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        return Fetch(url, HttpHosts.RequirePublic, ct);
    }

    private async Task<FetchedText> Fetch(Uri url, Action<Uri> validateHop, CancellationToken ct)
    {
        validateHop(url);
        var asked = url.Host;
        for (var hop = 0; ; hop++)
        {
            if (hop > 0)
            {
                validateHop(url);
            }

            Interlocked.Increment(ref _totalRequests);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                if (hop >= MaxRedirects)
                {
                    throw new HttpRequestException($"More than {MaxRedirects} redirects from {asked}.");
                }
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var text = await Read(response, ct).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            return new FetchedText(url, mediaType, text);
        }
    }

    // An IP in the url passes only when the allow-list names exactly that IP; a host pattern never covers one.
    private static void RequireNamedIfLiteral(Uri url, IReadOnlyList<string> allowed)
    {
        if (!url.IsAbsoluteUri || url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return;
        }
        var host = url.IdnHost.Trim('[', ']');
        if (!allowed.Any(entry => string.Equals(entry.Trim().Trim('[', ']'), host, StringComparison.OrdinalIgnoreCase)))
        {
            throw new HostNotAllowedException(url.Host);
        }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private async Task<string> Read(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxBytes)
        {
            throw new HttpRequestException($"The response is larger than {MaxBytes} bytes.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }
        if (total > MaxBytes)
        {
            throw new HttpRequestException($"The response is larger than {MaxBytes} bytes.");
        }
        Interlocked.Add(ref _totalBytesRead, total);
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
