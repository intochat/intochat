using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IntoChat.OS.Modules.Devices.Link;

internal sealed class DeviceLinkRevokedException(string message) : Exception(message);

// HTTP client for the PC -> cloud /circle/v1 channel. Signs every authenticated request with the
// machine-protected P-256 key and refreshes expiring tokens 60 s before expiry.
internal sealed class CloudClient(HttpClient http, DeviceCredentials credentials, TimeProvider clock)
{
    internal static readonly TimeSpan RefreshLeadTime = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task<EnrollResponse> EnrollAsync(Uri server, EnrollRequest request, byte[] privateKeyPkcs8, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(privateKeyPkcs8);

        var url = Combine(server, CircleRoutes.Enroll);
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent(request, DeviceWireJson.Default.EnrollRequest),
        };

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await TryReadEnrollErrorAsync(response, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Enrollment failed ({(int)response.StatusCode}): {reason}");
        }

        var enrolled = await ReadJsonAsync(response, DeviceWireJson.Default.EnrollResponse, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cloud returned an empty enrollment response.");

        var secrets = new LinkSecrets(
            server,
            enrolled.MachineCredentialId,
            enrolled.AccessToken,
            enrolled.AccessExpiresAt,
            enrolled.RefreshToken,
            enrolled.RefreshExpiresAt,
            privateKeyPkcs8);

        await credentials.Write(secrets, ct).ConfigureAwait(false);
        return enrolled;
    }

    public async Task<CircleStatusResponse> StatusAsync(CancellationToken ct = default)
    {
        using var response = await SendSignedAsync(
            HttpMethod.Get,
            CircleRoutes.Status,
            createContent: null,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.CircleStatusResponse, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cloud returned an empty status response.");
    }

    public async Task LeaveAsync(CancellationToken ct = default)
    {
        using var response = await SendSignedAsync(
            HttpMethod.Post,
            CircleRoutes.Leave,
            createContent: null,
            ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task ReportModelsAsync(ModelsReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var response = await SendSignedAsync(
            HttpMethod.Put,
            DeviceRoutes.Models,
            () => JsonContent(report, DeviceWireJson.Default.ModelsReport),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task<DeviceChatJob?> PollNextAsync(CancellationToken ct = default)
    {
        using var response = await SendSignedAsync(
            HttpMethod.Get,
            DeviceRoutes.Next,
            createContent: null,
            ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.DeviceChatJob, ct).ConfigureAwait(false);
    }

    public async Task<BrainDevicesResponse> BrainModelsAsync(string brain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        using var response = await SendSignedAsync(
            HttpMethod.Get,
            DeviceRoutes.Catalog + "?brain=" + Uri.EscapeDataString(brain),
            createContent: null,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.BrainDevicesResponse, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cloud returned an empty device models response.");
    }

    // The relay's NDJSON stream of DeviceChunk lines for one chat; the caller disposes the response.
    public async Task<HttpResponseMessage> BrainChatAsync(BrainChatRequest chat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return await SendSignedAsync(
            HttpMethod.Post,
            DeviceRoutes.Chat,
            () => JsonContent(chat, DeviceWireJson.Default.BrainChatRequest),
            ct,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
    }

    public async Task<bool> ReportComputeCapabilityAsync(ComputeCapabilityAdvertisement capability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        using var response = await SendSignedAsync(
            HttpMethod.Put,
            DeviceRoutes.ComputeCapability,
            () => JsonContent(capability, DeviceWireJson.Default.ComputeCapabilityAdvertisement),
            ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<BrainComputeNodesResponse?> BrainComputeNodesAsync(string brain, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        using var response = await SendSignedAsync(
            HttpMethod.Get,
            DeviceRoutes.ComputeNodes + "?brain=" + Uri.EscapeDataString(brain),
            createContent: null,
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
        using var availabilityCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        availabilityCts.CancelAfter(availabilityTimeout);

        using var response = await SendSignedAsync(
            HttpMethod.Post,
            DeviceRoutes.SandboxDispatch,
            () => JsonContent(dispatch, DeviceWireJson.Default.BrainSandboxDispatchRequest),
            availabilityCts.Token,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.ConnectedSandboxResponse, ct).ConfigureAwait(false);
    }

    public async Task<ConnectedSandboxRequest?> PollNextSandboxAsync(CancellationToken ct = default)
    {
        using var response = await SendSignedAsync(
            HttpMethod.Get,
            DeviceRoutes.SandboxNext,
            createContent: null,
            ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.ConnectedSandboxRequest, ct).ConfigureAwait(false);
    }

    public async Task ReportSandboxResultAsync(string jobId, ConnectedSandboxResponse result, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(result);
        using var response = await SendSignedAsync(
            HttpMethod.Post,
            DeviceRoutes.SandboxResultFor(jobId),
            () => JsonContent(result, DeviceWireJson.Default.ConnectedSandboxResponse),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task<ConnectedResourceResponse?> DispatchBrainResourceAsync(
        BrainResourceDispatchRequest dispatch,
        TimeSpan availabilityTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        using var availabilityCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        availabilityCts.CancelAfter(availabilityTimeout);

        using var response = await SendSignedAsync(
            HttpMethod.Post,
            DeviceRoutes.ResourceDispatch,
            () => JsonContent(dispatch, DeviceWireJson.Default.BrainResourceDispatchRequest),
            availabilityCts.Token,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.ConnectedResourceResponse, ct).ConfigureAwait(false);
    }

    public async Task<ConnectedResourceRequest?> PollNextResourceAsync(CancellationToken ct = default)
    {
        using var response = await SendSignedAsync(
            HttpMethod.Get,
            DeviceRoutes.ResourceNext,
            createContent: null,
            ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, DeviceWireJson.Default.ConnectedResourceRequest, ct).ConfigureAwait(false);
    }

    public async Task ReportResourceResultAsync(string jobId, ConnectedResourceResponse result, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(result);
        using var response = await SendSignedAsync(
            HttpMethod.Post,
            DeviceRoutes.ResourceResultFor(jobId),
            () => JsonContent(result, DeviceWireJson.Default.ConnectedResourceResponse),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task StreamAsync(string jobId, HttpContent ndjsonContent, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(ndjsonContent);

        var secrets = await EnsureFreshSecretsAsync(ct).ConfigureAwait(false);
        var url = Combine(secrets.Server, DeviceRoutes.StreamFor(jobId));
        using var request = BuildSignedRequest(HttpMethod.Post, url, secrets, ndjsonContent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await credentials.Revoke(ct).ConfigureAwait(false);
            throw new DeviceLinkRevokedException("The cloud rejected this device's credential.");
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        HttpMethod method,
        string route,
        Func<HttpContent>? createContent,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var (secrets, refreshed) = await EnsureFreshSecretsWithFlagAsync(ct).ConfigureAwait(false);
        var url = Combine(secrets.Server, route);

        var response = await SendOnceAsync(method, url, secrets, createContent?.Invoke(), ct, completion).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();

        if (refreshed)
        {
            await credentials.Revoke(ct).ConfigureAwait(false);
            throw new DeviceLinkRevokedException("The cloud rejected this device's credential.");
        }

        secrets = await RefreshAsync(secrets, ct).ConfigureAwait(false);
        var retry = await SendOnceAsync(method, url, secrets, createContent?.Invoke(), ct, completion).ConfigureAwait(false);
        if (retry.StatusCode == HttpStatusCode.Unauthorized)
        {
            retry.Dispose();
            await credentials.Revoke(ct).ConfigureAwait(false);
            throw new DeviceLinkRevokedException("The cloud rejected this device's credential.");
        }

        return retry;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        Uri url,
        LinkSecrets secrets,
        HttpContent? content,
        CancellationToken ct,
        HttpCompletionOption completion)
    {
        using var request = BuildSignedRequest(method, url, secrets, content);
        return await http.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    private async Task<LinkSecrets> EnsureFreshSecretsAsync(CancellationToken ct)
        => (await EnsureFreshSecretsWithFlagAsync(ct).ConfigureAwait(false)).Secrets;

    private async Task<(LinkSecrets Secrets, bool Refreshed)> EnsureFreshSecretsWithFlagAsync(CancellationToken ct)
    {
        var secrets = await credentials.Read(ct).ConfigureAwait(false)
            ?? throw new DeviceLinkRevokedException("This device has no active cloud credentials.");

        if (NeedsRefresh(secrets))
        {
            var refreshed = await RefreshAsync(secrets, ct).ConfigureAwait(false);
            return (refreshed, true);
        }

        return (secrets, false);
    }

    private bool NeedsRefresh(LinkSecrets secrets)
        => clock.GetUtcNow() >= secrets.AccessExpiresAt - RefreshLeadTime;

    private async Task<LinkSecrets> RefreshAsync(LinkSecrets observed, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await credentials.Read(ct).ConfigureAwait(false)
                ?? throw new DeviceLinkRevokedException("This device has no active cloud credentials.");

            if (!string.Equals(current.AccessToken, observed.AccessToken, StringComparison.Ordinal) && !NeedsRefresh(current))
            {
                return current;
            }

            var url = Combine(current.Server, CircleRoutes.Refresh);
            using var content = JsonContent(new RefreshRequest(current.RefreshToken), DeviceWireJson.Default.RefreshRequest);
            using var request = BuildSignedRequest(HttpMethod.Post, url, current, content);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await credentials.Revoke(ct).ConfigureAwait(false);
                throw new DeviceLinkRevokedException("Cloud refresh rejected this device's credential.");
            }

            response.EnsureSuccessStatusCode();
            var rotated = await ReadJsonAsync(response, DeviceWireJson.Default.EnrollResponse, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Cloud returned an empty refresh response.");

            var updated = new LinkSecrets(
                current.Server,
                rotated.MachineCredentialId,
                rotated.AccessToken,
                rotated.AccessExpiresAt,
                rotated.RefreshToken,
                rotated.RefreshExpiresAt,
                current.PrivateKeyPkcs8);

            await credentials.Write(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private HttpRequestMessage BuildSignedRequest(HttpMethod method, Uri url, LinkSecrets secrets, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.AccessToken);

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(secrets.PrivateKeyPkcs8, out _);
        var proof = MachineProof.Sign(key, method.Method, url, secrets.AccessToken, clock.GetUtcNow());
        request.Headers.TryAddWithoutValidation(CircleRoutes.MachineProofHeader, proof);
        return request;
    }

    private static StringContent JsonContent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize(text, typeInfo);
    }

    private static async Task<string> TryReadEnrollErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return response.ReasonPhrase ?? response.StatusCode.ToString();
        }

        try
        {
            var error = JsonSerializer.Deserialize(text, DeviceWireJson.Default.EnrollError);
            return error is not null ? error.Kind.ToString() : text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    internal static Uri Combine(Uri server, string route)
    {
        var baseText = server.AbsoluteUri;
        if (!baseText.EndsWith('/'))
        {
            baseText += "/";
        }
        return new Uri(new Uri(baseText, UriKind.Absolute), route.TrimStart('/'));
    }
}
