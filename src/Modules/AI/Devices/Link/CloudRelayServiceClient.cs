using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Multi-tenant HTTP client for Hosted Cloud OS -&gt; cloud <c>/circle/v1/compute/*</c> relay calls.
/// Authenticates each request with a short-lived ES256 <c>ServiceAssertion</c> JWT (<c>Authorization: Service &lt;jwt&gt;</c>)
/// scoped to the target <c>brainId</c> (<c>purpose: "circle-compute-dispatch"</c>), used when the host has no
/// single-machine <c>devices/link.bin</c> credential.
/// </summary>
internal sealed class CloudRelayServiceClient(HttpClient http, IServiceAssertionSigner? signer)
{
    internal const string ComputeDispatchPurpose = "circle-compute-dispatch";

    public bool IsConfigured => signer is { IsConfigured: true, ServiceUri: not null };

    public async Task<BrainDevicesResponse> BrainModelsAsync(string brain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        using var request = BuildAssertedRequest(
            brain,
            HttpMethod.Get,
            DeviceRoutes.Catalog + "?brain=" + Uri.EscapeDataString(brain),
            bodyBytes: null);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.BrainDevicesResponse, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cloud returned an empty device models response.");
    }

    // The relay's NDJSON stream of DeviceChunk lines for one chat; the caller disposes the response.
    public async Task<HttpResponseMessage> BrainChatAsync(BrainChatRequest chat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentException.ThrowIfNullOrWhiteSpace(chat.Brain);
        var bodyBytes = SerializeJson(chat, DeviceWireJson.Default.BrainChatRequest);
        using var request = BuildAssertedRequest(chat.Brain, HttpMethod.Post, DeviceRoutes.Chat, bodyBytes);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    public async Task<BrainComputeNodesResponse?> BrainComputeNodesAsync(string brain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        using var request = BuildAssertedRequest(
            brain,
            HttpMethod.Get,
            DeviceRoutes.ComputeNodes + "?brain=" + Uri.EscapeDataString(brain),
            bodyBytes: null);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
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
        using var request = BuildAssertedRequest(dispatch.Brain, HttpMethod.Post, DeviceRoutes.SandboxDispatch, bodyBytes);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, availabilityCts.Token).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
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
        using var request = BuildAssertedRequest(dispatch.Brain, HttpMethod.Post, DeviceRoutes.ResourceDispatch, bodyBytes);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, availabilityCts.Token).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.PcResourceResponse, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildAssertedRequest(string brainId, HttpMethod method, string route, byte[]? bodyBytes)
    {
        if (signer is not { IsConfigured: true, ServiceUri: { } baseUri })
        {
            throw new InvalidOperationException("Cloud relay service-assertion signing is not configured.");
        }

        var url = CloudClient.Combine(baseUri, route);
        var token = signer.Sign(brainId, ComputeDispatchPurpose, method.Method, url.PathAndQuery, bodyBytes ?? []);
        var request = new HttpRequestMessage(method, url);
        if (bodyBytes is not null)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        request.Headers.Authorization = new AuthenticationHeaderValue(ServiceAssertion.Scheme, token);
        return request;
    }

    private static byte[] SerializeJson<T>(T value, JsonTypeInfo<T> typeInfo)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, typeInfo));

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize(text, typeInfo);
    }
}
