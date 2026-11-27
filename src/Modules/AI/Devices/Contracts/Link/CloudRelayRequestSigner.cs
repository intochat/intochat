using System.Net.Http.Headers;
using System.Security.Cryptography;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Shared dual-mode HTTP request signer for outbound OS -&gt; Cloud <c>/circle/v1/*</c> relay calls.
/// Unifies ES256 <c>ServiceAssertion</c> (<c>Authorization: Service &lt;jwt&gt;</c>) and single-machine
/// <c>Bearer</c> + <c>X-Machine-Proof</c> header signing across module and kernel relay transports.
/// </summary>
public static class CloudRelayRequestSigner
{
    /// <summary>
    /// Combines a relay base URI with a relative <c>/circle/v1/*</c> route.
    /// </summary>
    public static Uri Combine(Uri server, string route)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(route);

        var baseText = server.AbsoluteUri;
        if (!baseText.EndsWith('/'))
        {
            baseText += "/";
        }

        return new Uri(new Uri(baseText, UriKind.Absolute), route.TrimStart('/'));
    }

    /// <summary>
    /// Signs <paramref name="request"/> with a short-lived ES256 <c>ServiceAssertion</c> JWT when
    /// <paramref name="signer"/> is configured, <paramref name="brainId"/> is non-empty, and
    /// <see cref="HttpRequestMessage.RequestUri"/> is set.
    /// </summary>
    public static bool TrySignWithServiceAssertion(
        HttpRequestMessage request,
        IServiceAssertionSigner? signer,
        string? brainId,
        string purpose,
        ReadOnlySpan<byte> bodyBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (signer is not { IsConfigured: true }
            || string.IsNullOrWhiteSpace(brainId)
            || request.RequestUri is null)
        {
            return false;
        }

        var token = signer.Sign(
            brainId,
            purpose,
            request.Method.Method,
            request.RequestUri.PathAndQuery,
            bodyBytes);
        request.Headers.Authorization = new AuthenticationHeaderValue(ServiceAssertion.Scheme, token);
        return true;
    }

    /// <summary>
    /// Async overload of <see cref="TrySignWithServiceAssertion"/> that reads body bytes from
    /// <see cref="HttpRequestMessage.Content"/> when present.
    /// </summary>
    public static async ValueTask<bool> TrySignWithServiceAssertionAsync(
        HttpRequestMessage request,
        IServiceAssertionSigner? signer,
        string? brainId,
        string purpose,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (signer is not { IsConfigured: true }
            || string.IsNullOrWhiteSpace(brainId)
            || request.RequestUri is null)
        {
            return false;
        }

        var bodyBytes = request.Content is not null
            ? await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)
            : [];

        return TrySignWithServiceAssertion(request, signer, brainId, purpose, bodyBytes);
    }

    /// <summary>
    /// Signs <paramref name="request"/> with <c>Authorization: Bearer &lt;accessToken&gt;</c> and a P-256
    /// <c>X-Machine-Proof</c> header generated from <paramref name="privateKeyPkcs8"/>.
    /// </summary>
    public static void SignWithMachineProof(
        HttpRequestMessage request,
        string accessToken,
        byte[] privateKeyPkcs8,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(privateKeyPkcs8);
        ArgumentNullException.ThrowIfNull(clock);
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("Request URI must be set before signing with machine proof.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        var proof = MachineProof.Sign(key, request.Method.Method, request.RequestUri, accessToken, clock.GetUtcNow());
        request.Headers.TryAddWithoutValidation(CircleRoutes.MachineProofHeader, proof);
    }

    /// <summary>
    /// Signs <paramref name="request"/> with <c>Authorization: Bearer &lt;bearerToken&gt;</c> and an optional
    /// <c>X-Machine-Proof</c> header produced by <paramref name="machineProofFactory"/>.
    /// </summary>
    public static async ValueTask<bool> TrySignWithBearerAsync(
        HttpRequestMessage request,
        string? bearerToken,
        Func<HttpRequestMessage, byte[], CancellationToken, ValueTask<string?>>? machineProofFactory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return false;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (machineProofFactory is not null)
        {
            var bodyBytes = request.Content is not null
                ? await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)
                : [];
            var proof = await machineProofFactory(request, bodyBytes, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(proof))
            {
                request.Headers.TryAddWithoutValidation(CircleRoutes.MachineProofHeader, proof);
            }
        }

        return true;
    }

    /// <summary>
    /// Authorizes <paramref name="request"/> with <c>ServiceAssertion</c> when configured for
    /// <paramref name="brainId"/>, falling back to <c>Bearer</c> + optional <c>X-Machine-Proof</c>.
    /// </summary>
    public static async ValueTask AuthorizeServiceOrBearerAsync(
        HttpRequestMessage request,
        IServiceAssertionSigner? signer,
        string? brainId,
        string purpose,
        string? bearerToken,
        Func<HttpRequestMessage, byte[], CancellationToken, ValueTask<string?>>? machineProofFactory = null,
        CancellationToken ct = default)
    {
        if (await TrySignWithServiceAssertionAsync(request, signer, brainId, purpose, ct).ConfigureAwait(false))
        {
            return;
        }

        await TrySignWithBearerAsync(request, bearerToken, machineProofFactory, ct).ConfigureAwait(false);
    }
}
