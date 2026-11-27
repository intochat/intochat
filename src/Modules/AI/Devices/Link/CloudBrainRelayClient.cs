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
    IServiceAssertionSigner? signer = null) : ICloudBrainRelayClient
{
    internal const string ComputeDispatchPurpose = "circle-compute-dispatch";

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
