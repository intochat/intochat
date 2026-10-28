using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using IntoChat.OS.Modules.Circles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.Cloud.Circle.Server;

// D-02, D-09: a machine presents an invite code with its P-256 public key and receives a circle credential bound to
// that key; refresh rotates both tokens. Responses name only the UI failure classes (rejected, expired, rate_limited)
// or carry fixed text. Nothing here logs a code, token or key.
public sealed partial class CircleServerModule
{
    internal const int MaxBodyBytes = 8 * 1024;
    private const int MaxFieldLength = 256;

    private static void MapEnrollment(IEndpointRouteBuilder routes)
    {
        routes.MapPost(CircleRoutes.Enroll, Enroll).AddEndpointFilter(CircleResponses.Filter);
        routes.MapPost(CircleRoutes.Refresh, Refresh).AddEndpointFilter(CircleResponses.Filter);
    }

    private static async Task<IResult> Enroll(HttpContext http, ICircleInviteRedeemer redeemer, EnrollmentRateLimits limits, IGrainFactory grains)
    {
        var request = await CircleResponses.ReadBody(http, CircleJsonContext.Default.EnrollRequest);
        if (request is null || !TryKey(request, out var spki)) { return CircleResponses.Malformed; }

        // Before any redeem, so a guessing client spends its window, not a code.
        if (!limits.TryAddress(AddressOf(http))) { return CircleResponses.Enroll(EnrollErrorKind.RateLimited); }
        var thumbprint = InviteCode.Thumbprint(spki);
        var outcome = await redeemer.Redeem(request.Code, request.MachineId, thumbprint, request.MachineName);
        switch (outcome.Result)
        {
            case RedeemResult.Expired: return CircleResponses.Enroll(EnrollErrorKind.Expired);
            case RedeemResult.Accepted or RedeemResult.Repeat: break;
            default: return CircleResponses.Enroll(EnrollErrorKind.Rejected);
        }
        // The code is spent; within the invite repeat window the same machine and key may try again.
        if (!limits.TryCircle(outcome.CircleId)) { return CircleResponses.Enroll(EnrollErrorKind.RateLimited); }

        var credential = grains.GetGrain<IMachineCredential>(MachineTokens.CredentialIdFor(outcome.CircleId, request.MachineId));
        var tokens = await credential.Enroll(new EnrollGrant(outcome.CircleId, outcome.CircleName, outcome.Role, outcome.AccountId,
            outcome.PendingAccount, request.MachineId, request.MachineName, spki, thumbprint, outcome.Result == RedeemResult.Repeat));
        return tokens is null ? CircleResponses.Enroll(EnrollErrorKind.Rejected) : CircleResponses.Issued(tokens);
    }

    // The refresh carries a proof signed by the bound key; its token binding is the bearer when one is sent (an
    // expired access token is fine), otherwise the refresh token itself.
    private static async Task<IResult> Refresh(HttpContext http, IGrainFactory grains, INonceCache nonces)
    {
        var request = await CircleResponses.ReadBody(http, CircleJsonContext.Default.RefreshRequest);
        if (request is null || string.IsNullOrEmpty(request.RefreshToken)) { return CircleResponses.Malformed; }
        if (!MachineTokens.TryCredentialId(request.RefreshToken, out var credentialId)) { return CircleResponses.Unauthorized; }
        var credential = grains.GetGrain<IMachineCredential>(credentialId);
        var key = await credential.PublicKey();
        var bound = MachineAuthentication.Bearer(http.Request) ?? request.RefreshToken;
        if (key.Length == 0 || !await MachineAuthentication.ProofHolds(http, key, bound, credential, nonces)) { return CircleResponses.Unauthorized; }

        var outcome = await credential.Refresh(request.RefreshToken);
        return outcome is { Result: RefreshResult.Rotated, Tokens: { } tokens } ? CircleResponses.Issued(tokens) : CircleResponses.Unauthorized;
    }

    private static bool TryKey(EnrollRequest request, out byte[] spki)
    {
        spki = [];
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > MaxFieldLength
            || string.IsNullOrWhiteSpace(request.MachineId) || request.MachineId.Length > MaxFieldLength
            || (request.MachineName?.Length ?? 0) > MaxFieldLength || (request.OsDescription?.Length ?? 0) > MaxFieldLength
            || string.IsNullOrEmpty(request.MachinePublicKeySpki) || request.MachinePublicKeySpki.Length > MaxFieldLength * 2)
        {
            return false;
        }
        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(request.MachinePublicKeySpki);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.KeySize != 256
                || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                return false;
            }
            spki = bytes;
            return true;
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    private static string AddressOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// The fixed answers every /circle/v1 route gives, and the per-route filter: HTTPS (loopback excepted), a Date header
// for client skew correction, and no caching.
internal static class CircleResponses
{
    public static IResult Malformed { get; } = Results.Text("Malformed request.", statusCode: StatusCodes.Status400BadRequest);
    public static IResult Unauthorized { get; } = Results.Text("Machine authentication failed.", statusCode: StatusCodes.Status401Unauthorized);
    public static IResult Forbidden { get; } = Results.Text("This machine is waiting for an account.", statusCode: StatusCodes.Status403Forbidden);
    private static readonly IResult TooLarge = Results.Text("Request too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
    private static readonly IResult HttpsRequired = Results.Text("HTTPS required.", statusCode: StatusCodes.Status400BadRequest);

    public static IResult Enroll(EnrollErrorKind kind) => Results.Json(new EnrollError(kind), CircleJsonContext.Default.EnrollError, statusCode: kind switch
    {
        EnrollErrorKind.Expired => StatusCodes.Status410Gone,
        EnrollErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status409Conflict,
    });

    public static IResult Issued(IssuedTokens tokens)
        => Results.Json(new EnrollResponse(tokens.CredentialId, tokens.AccessToken, tokens.AccessExpiresAt, tokens.RefreshToken, tokens.RefreshExpiresAt,
            new CircleInfo(tokens.CircleId, tokens.CircleName, tokens.Role), tokens.PendingAccount,
            new CircleEndpoints(CircleRoutes.Journal, CircleRoutes.Store, CircleRoutes.Files)), CircleJsonContext.Default.EnrollResponse);

    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Date = CircleServerModule.Clock(http.RequestServices).GetUtcNow().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (!http.Request.IsHttps && http.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote)) { return HttpsRequired; }
        if (http.Request.ContentLength > CircleServerModule.MaxBodyBytes) { return TooLarge; }
        return await next(context);
    }

    // At most MaxBodyBytes of JSON; anything else reads as null (a 400).
    public static async Task<T?> ReadBody<T>(HttpContext http, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) { limit.MaxRequestBodySize = CircleServerModule.MaxBodyBytes; }
        if (!http.Request.HasJsonContentType()) { return null; }
        try { return await http.Request.ReadFromJsonAsync(type, http.RequestAborted); }
        catch (JsonException) { return null; }
        catch (BadHttpRequestException) { return null; }
    }

    // The absolute URL a proof signs, as the client addressed it (forwarded headers already applied by the host).
    public static Uri RequestUrl(HttpRequest request) => new(request.GetEncodedUrl());
}
