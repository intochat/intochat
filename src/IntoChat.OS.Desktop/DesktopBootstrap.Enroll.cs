using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class DesktopBootstrap
{
    public const string EnrollProtocolScheme = "intochat";
    public const string EnrollProtocolHost = "enroll";

    /// <summary>
    /// Extracts an enrollment URI from <c>--enroll &lt;uri&gt;</c>, <c>--enroll=&lt;uri&gt;</c>,
    /// or a direct protocol activation argument (<c>intochat://enroll?...</c>).
    /// </summary>
    public static bool TryExtractEnrollUri(ReadOnlySpan<string> args, out string? enrollUri)
    {
        enrollUri = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (string.Equals(arg, "--enroll", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    enrollUri = args[i + 1].Trim();
                    return true;
                }

                enrollUri = string.Empty;
                return true;
            }

            const string prefix = "--enroll=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                enrollUri = arg[prefix.Length..].Trim();
                return true;
            }

            if (arg.StartsWith($"{EnrollProtocolScheme}://", StringComparison.OrdinalIgnoreCase))
            {
                enrollUri = arg.Trim();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates and parses an <c>intochat://enroll?server=...&amp;code=...</c> URI.
    /// Rejects non-https servers (except loopback http for local dev), embedded credentials, and malformed codes.
    /// Never includes raw codes in error messages.
    /// </summary>
    public static bool TryParseEnrollUri(string? rawUri, out Uri? server, out string? code, out string? error)
    {
        server = null;
        code = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawUri))
        {
            error = "Enrollment URI is required.";
            return false;
        }

        if (!Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "Enrollment URI is not a valid absolute URI.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, EnrollProtocolScheme, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Enrollment URI must use the '{EnrollProtocolScheme}://' scheme.";
            return false;
        }

        if (!string.Equals(parsed.Host, EnrollProtocolHost, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Enrollment URI host must be '{EnrollProtocolHost}'.";
            return false;
        }

        string? rawServer = null;
        string? rawCode = null;
        var query = parsed.Query.AsSpan().TrimStart('?');
        while (!query.IsEmpty)
        {
            var amp = query.IndexOf('&');
            var segment = amp >= 0 ? query[..amp] : query;
            query = amp >= 0 ? query[(amp + 1)..] : default;

            if (segment.IsEmpty)
            {
                continue;
            }

            var eq = segment.IndexOf('=');
            var keySpan = eq >= 0 ? segment[..eq] : segment;
            var valSpan = eq >= 0 ? segment[(eq + 1)..] : default;
            var key = Uri.UnescapeDataString(keySpan.ToString().Replace('+', ' '));
            var val = Uri.UnescapeDataString(valSpan.ToString().Replace('+', ' '));

            if (string.Equals(key, "server", StringComparison.OrdinalIgnoreCase))
            {
                rawServer = val;
            }
            else if (string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
            {
                rawCode = val;
            }
        }

        if (string.IsNullOrWhiteSpace(rawServer)
            || !Uri.TryCreate(rawServer.Trim(), UriKind.Absolute, out var candidateServer)
            || !string.IsNullOrEmpty(candidateServer.UserInfo)
            || string.IsNullOrWhiteSpace(candidateServer.Host))
        {
            error = "Server URL must be an absolute https:// URI (or loopback http:// for local development) without credentials.";
            return false;
        }

        var isHttps = string.Equals(candidateServer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(candidateServer.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && candidateServer.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            error = "Server URL must use https:// (plain http:// is allowed only for localhost/loopback development).";
            return false;
        }

        if (!string.IsNullOrEmpty(candidateServer.Query)
            || !string.IsNullOrEmpty(candidateServer.Fragment)
            || (!string.IsNullOrEmpty(candidateServer.AbsolutePath) && !string.Equals(candidateServer.AbsolutePath, "/", StringComparison.Ordinal)))
        {
            error = "Server URL must be an origin only (scheme, host, and optional port) without path, query, or fragment.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawCode))
        {
            error = "Pairing code is required.";
            return false;
        }

        var trimmedCode = rawCode.Trim();
        var compactCount = 0;
        foreach (var ch in trimmedCode)
        {
            if (ch is '-' or ' ')
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(ch) || compactCount >= 32)
            {
                error = "Pairing code contains invalid characters.";
                return false;
            }

            compactCount++;
        }

        if (compactCount < 6)
        {
            error = "Pairing code must be at least 6 characters.";
            return false;
        }

        server = new Uri(candidateServer.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        code = trimmedCode;
        return true;
    }

    public static string GetSingleInstancePipeName(DesktopLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var normalized = Path.GetFullPath(layout.HomeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"IntoChatOS.Desktop.Ipc.{hash}";
    }

    public static async Task<bool> TryForwardEnrollUriAsync(
        string pipeName,
        string enrollUri,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollUri);

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            var connectTimeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 50, 5_000);
            await client.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync(enrollUri.AsMemory(), cancellationToken).ConfigureAwait(false);
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(5));
            var ack = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            return string.Equals(ack?.Trim(), "OK", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Trace.TraceWarning("Could not forward enrollment URI over desktop single-instance pipe: {0}", ex.Message);
            return false;
        }
    }

    public static IDisposable StartEnrollPipeServer(
        string pipeName,
        Func<string, CancellationToken, Task<bool>> onEnrollUri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(onEnrollUri);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunEnrollPipeLoopAsync(pipeName, onEnrollUri, cts.Token), cts.Token);
        return new PipeServerRegistration(cts);
    }

    private static async Task RunEnrollPipeLoopAsync(
        string pipeName,
        Func<string, CancellationToken, Task<bool>> onEnrollUri,
        CancellationToken cancellationToken)
    {
        var pipeOptions = OperatingSystem.IsWindows()
            ? PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            : PipeOptions.Asynchronous;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    pipeOptions);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var ok = !string.IsNullOrWhiteSpace(line)
                    && TryParseEnrollUri(line, out _, out _, out _)
                    && await onEnrollUri(line.Trim(), cancellationToken).ConfigureAwait(false);

                await writer.WriteLineAsync(ok ? "OK" : "ERR").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Trace.TraceWarning("Desktop single-instance IPC pipe loop encountered an error: {0}", ex.Message);
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public static async Task<(bool Succeeded, string? Error)> SendEnrollToKernelAsync(
        HttpClient http,
        Uri kernelUrl,
        string presenceToken,
        string enrollUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(kernelUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(presenceToken);

        if (!TryParseEnrollUri(enrollUri, out _, out _, out var parseError))
        {
            return (false, parseError ?? "Invalid enrollment URI.");
        }

        byte[] payloadBytes;
        using (var buffer = new MemoryStream())
        {
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("uri", enrollUri.Trim());
                json.WriteEndObject();
            }

            payloadBytes = buffer.ToArray();
        }

        // Anti-drive-by: protocol handoffs stage a pending enrollment for explicit user confirmation
        // in Desktop Settings (displaying the exact server origin) instead of silently auto-joining.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(kernelUrl, "brains/owner/devices/link/pending"))
        {
            Content = new ByteArrayContent(payloadBytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(PresenceHeaderName, presenceToken);
        request.Headers.TryAddWithoutValidation(CanonicalPresenceHeaderName, presenceToken);
        request.Headers.TryAddWithoutValidation("Origin", kernelUrl.GetLeftPart(UriPartial.Authority));

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            return (false, $"Device pairing failed with HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Trace.TraceWarning("Could not reach local kernel for device pairing: {0}", ex.Message);
            return (false, $"Could not reach local IntoChat OS kernel to complete device pairing: {ex.Message}");
        }
    }

    private sealed class PipeServerRegistration(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                Trace.TraceWarning("Desktop IPC pipe server cancellation source was already disposed: {0}", ex.Message);
            }
            finally
            {
                cts.Dispose();
            }
        }
    }
}
