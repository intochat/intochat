using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Devices.Link;

internal sealed class CloudBrainRelayClient(
    HttpClient http,
    CloudClient cloud,
    DeviceCredentials credentials,
    IServiceAssertionSigner? signer = null,
    TimeProvider? clock = null,
    TimeSpan? engineeringIdleTimeout = null) : ICloudBrainRelayClient
{
    internal const string ComputeDispatchPurpose = "circle-compute-dispatch";
    internal const int MaxEngineeringStreamBytes = 16 * 1024 * 1024;
    internal const int MaxEngineeringLineBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan DefaultEngineeringIdleTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan MaxEngineeringTotalTimeout = TimeSpan.FromSeconds(900);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _engineeringIdleTimeout = engineeringIdleTimeout is { } idle && idle > TimeSpan.Zero
        ? (idle <= DefaultEngineeringIdleTimeout ? idle : DefaultEngineeringIdleTimeout)
        : DefaultEngineeringIdleTimeout;

    public async ValueTask<bool> IsConfiguredAsync(CancellationToken ct = default)
        => await credentials.Read(ct).ConfigureAwait(false) is not null
            || signer is { IsConfigured: true, ServiceUri: not null };

    public async Task<BrainDevicesResponse> BrainModelsAsync(string brain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        using var response = await SendRelayAsync(
            brain,
            HttpMethod.Get,
            DeviceRoutes.Catalog + "?brain=" + Uri.EscapeDataString(brain),
            bodyBytes: null,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.BrainDevicesResponse, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cloud returned an empty device models response.");
    }

    public async Task<HttpResponseMessage> BrainChatAsync(BrainChatRequest chat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentException.ThrowIfNullOrWhiteSpace(chat.Brain);
        var bodyBytes = SerializeJson(chat, DeviceWireJson.Default.BrainChatRequest);
        return await SendRelayAsync(
            chat.Brain,
            HttpMethod.Post,
            DeviceRoutes.Chat,
            bodyBytes,
            ct,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
    }

    public async Task<BrainComputeNodesResponse?> BrainComputeNodesAsync(string brain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        using var response = await SendRelayAsync(
            brain,
            HttpMethod.Get,
            DeviceRoutes.ComputeNodes + "?brain=" + Uri.EscapeDataString(brain),
            bodyBytes: null,
            ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.BrainComputeNodesResponse, ct).ConfigureAwait(false);
    }

    public async Task<ConnectedSandboxResponse?> DispatchBrainSandboxAsync(
        BrainSandboxDispatchRequest dispatch,
        TimeSpan availabilityTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatch.Brain);

        using var availabilityCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        availabilityCts.CancelAfter(availabilityTimeout);

        var bodyBytes = SerializeJson(dispatch, DeviceWireJson.Default.BrainSandboxDispatchRequest);
        using var response = await SendRelayAsync(
            dispatch.Brain,
            HttpMethod.Post,
            DeviceRoutes.SandboxDispatch,
            bodyBytes,
            availabilityCts.Token,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (IsUnavailableStatus(response.StatusCode))
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.ConnectedSandboxResponse, ct).ConfigureAwait(false);
    }

    public async Task<PcResourceResponse?> DispatchBrainResourceAsync(
        BrainResourceDispatchRequest dispatch,
        TimeSpan availabilityTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatch.Brain);

        using var availabilityCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        availabilityCts.CancelAfter(availabilityTimeout);

        var bodyBytes = SerializeJson(dispatch, DeviceWireJson.Default.BrainResourceDispatchRequest);
        using var response = await SendRelayAsync(
            dispatch.Brain,
            HttpMethod.Post,
            DeviceRoutes.ResourceDispatch,
            bodyBytes,
            availabilityCts.Token,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (IsUnavailableStatus(response.StatusCode))
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.PcResourceResponse, ct).ConfigureAwait(false);
    }

    public async Task<ConnectedEngineeringResponse?> DispatchBrainEngineeringAsync(
        BrainEngineeringDispatchRequest dispatch,
        TimeSpan availabilityTimeout,
        Func<EngineeringStreamChunk, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatch.Brain);

        var requestedSeconds = Math.Clamp(dispatch.Request.TimeoutSeconds, 15, (int)MaxEngineeringTotalTimeout.TotalSeconds);
        var requestedCeiling = TimeSpan.FromSeconds(requestedSeconds);
        var totalBudget = availabilityTimeout > TimeSpan.Zero && availabilityTimeout < requestedCeiling
            ? availabilityTimeout
            : requestedCeiling;
        if (totalBudget > MaxEngineeringTotalTimeout)
        {
            totalBudget = MaxEngineeringTotalTimeout;
        }

        using var totalTimeoutCts = new CancellationTokenSource(totalBudget, _clock);
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct, totalTimeoutCts.Token);
        var totalCt = totalCts.Token;

        var bodyBytes = SerializeJson(dispatch, DeviceWireJson.Default.BrainEngineeringDispatchRequest);
        HttpResponseMessage response;
        try
        {
            response = await SendRelayAsync(
                dispatch.Brain,
                HttpMethod.Post,
                DeviceRoutes.EngineeringDispatch,
                bodyBytes,
                totalCt,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden || IsUnavailableStatus(response.StatusCode))
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase))
            {
                return await ReadBoundedEngineeringStreamAsync(
                    response,
                    dispatch,
                    onProgress,
                    totalCt,
                    ct).ConfigureAwait(false);
            }

            return await ReadBoundedEngineeringJsonAsync(
                response,
                dispatch,
                totalCt,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<ConnectedEngineeringResponse?> ReadBoundedEngineeringStreamAsync(
        HttpResponseMessage response,
        BrainEngineeringDispatchRequest dispatch,
        Func<EngineeringStreamChunk, Task>? onProgress,
        CancellationToken totalCt,
        CancellationToken callerCt)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(totalCt).ConfigureAwait(false);
            var buffer = new byte[8192];
            using var lineBuffer = new MemoryStream(capacity: 4096);
            long totalBytesRead = 0;
            ConnectedEngineeringResponse? terminal = null;
            string? lastError = null;

            while (true)
            {
                int bytesRead;
                using (var idleTimeoutCts = new CancellationTokenSource(_engineeringIdleTimeout, _clock))
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(totalCt, idleTimeoutCts.Token))
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
                if (totalBytesRead > MaxEngineeringStreamBytes)
                {
                    return new ConnectedEngineeringResponse(
                        Ok: false,
                        RunId: dispatch.Request.RunId,
                        Error: "stream-size-limit-exceeded: Connected PC engineering stream exceeded 16 MiB cap.",
                        ExecutedByMachineId: dispatch.MachineId);
                }

                var segmentStart = 0;
                for (var i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    var chunkLen = i - segmentStart;
                    if (lineBuffer.Length + chunkLen > MaxEngineeringLineBytes)
                    {
                        return new ConnectedEngineeringResponse(
                            Ok: false,
                            RunId: dispatch.Request.RunId,
                            Error: "stream-line-limit-exceeded: Connected PC engineering NDJSON line exceeded 4 MiB cap.",
                            ExecutedByMachineId: dispatch.MachineId);
                    }

                    if (chunkLen > 0)
                    {
                        lineBuffer.Write(buffer, segmentStart, chunkLen);
                    }

                    await ProcessEngineeringLineAsync(lineBuffer, onProgress, chunk =>
                    {
                        if (!string.IsNullOrWhiteSpace(chunk.Error))
                        {
                            lastError = chunk.Error;
                        }

                        if (chunk.Result is not null)
                        {
                            terminal = chunk.Result;
                        }
                    }).ConfigureAwait(false);

                    lineBuffer.SetLength(0);
                    segmentStart = i + 1;
                }

                var remaining = bytesRead - segmentStart;
                if (remaining > 0)
                {
                    if (lineBuffer.Length + remaining > MaxEngineeringLineBytes)
                    {
                        return new ConnectedEngineeringResponse(
                            Ok: false,
                            RunId: dispatch.Request.RunId,
                            Error: "stream-line-limit-exceeded: Connected PC engineering NDJSON line exceeded 4 MiB cap.",
                            ExecutedByMachineId: dispatch.MachineId);
                    }

                    lineBuffer.Write(buffer, segmentStart, remaining);
                }
            }

            if (lineBuffer.Length > 0)
            {
                await ProcessEngineeringLineAsync(lineBuffer, onProgress, chunk =>
                {
                    if (!string.IsNullOrWhiteSpace(chunk.Error))
                    {
                        lastError = chunk.Error;
                    }

                    if (chunk.Result is not null)
                    {
                        terminal = chunk.Result;
                    }
                }).ConfigureAwait(false);
            }

            return terminal ?? new ConnectedEngineeringResponse(
                Ok: false,
                RunId: dispatch.Request.RunId,
                Error: lastError ?? "Connected PC engineering stream ended without a terminal result.",
                ExecutedByMachineId: dispatch.MachineId);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task ProcessEngineeringLineAsync(
        MemoryStream lineBuffer,
        Func<EngineeringStreamChunk, Task>? onProgress,
        Action<EngineeringStreamChunk> onChunk)
    {
        var line = Encoding.UTF8.GetString(lineBuffer.GetBuffer(), 0, (int)lineBuffer.Length).Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var chunk = JsonSerializer.Deserialize(line, DeviceWireJson.Default.EngineeringStreamChunk);
        if (chunk is null)
        {
            return;
        }

        if (onProgress is not null)
        {
            await onProgress(chunk).ConfigureAwait(false);
        }

        onChunk(chunk);
    }

    private async Task<ConnectedEngineeringResponse?> ReadBoundedEngineeringJsonAsync(
        HttpResponseMessage response,
        BrainEngineeringDispatchRequest dispatch,
        CancellationToken totalCt,
        CancellationToken callerCt)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(totalCt).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            long totalBytesRead = 0;

            while (true)
            {
                int bytesRead;
                using (var idleTimeoutCts = new CancellationTokenSource(_engineeringIdleTimeout, _clock))
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(totalCt, idleTimeoutCts.Token))
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
                if (totalBytesRead > MaxEngineeringStreamBytes)
                {
                    return new ConnectedEngineeringResponse(
                        Ok: false,
                        RunId: dispatch.Request.RunId,
                        Error: "stream-size-limit-exceeded: Connected PC engineering response exceeded 16 MiB cap.",
                        ExecutedByMachineId: dispatch.MachineId);
                }

                body.Write(buffer, 0, bytesRead);
            }

            if (body.Length == 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize(text, DeviceWireJson.Default.ConnectedEngineeringResponse);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendRelayAsync(
        string brainId,
        HttpMethod method,
        string route,
        byte[]? bodyBytes,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        // Single-machine device link takes precedence over hosted service-assertion auth.
        if (await credentials.Read(ct).ConfigureAwait(false) is not null)
        {
            Func<HttpContent>? createContent = bodyBytes is not null
                ? () => CreateJsonContent(bodyBytes)
                : null;
            return await cloud.SendSignedAsync(method, route, createContent, ct, completion).ConfigureAwait(false);
        }

        if (signer is not { IsConfigured: true, ServiceUri: { } baseUri })
        {
            throw new DeviceUnavailableException("relay-unconfigured");
        }

        var url = CloudRelayRequestSigner.Combine(baseUri, route);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = bodyBytes is not null ? CreateJsonContent(bodyBytes) : null,
        };
        CloudRelayRequestSigner.TrySignWithServiceAssertion(
            request,
            signer,
            brainId,
            ComputeDispatchPurpose,
            bodyBytes ?? []);

        return await http.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    private static bool IsUnavailableStatus(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Gone
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static ByteArrayContent CreateJsonContent(byte[] bodyBytes)
    {
        var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static byte[] SerializeJson<T>(T value, JsonTypeInfo<T> typeInfo)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, typeInfo));

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize(text, typeInfo);
    }
}
