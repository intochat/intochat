using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DigitalBrain.Capabilities.Identity;

/// <summary>What a trusted issuer asserts about one HTTP request it makes to an install.</summary>
/// <param name="Issuer">The issuer name (header <c>kid</c>, payload <c>iss</c>).</param>
/// <param name="Audience">The install origin the request is for.</param>
/// <param name="Operation">The one operation the assertion admits, for example <c>devices.enrollment.redeem</c>.</param>
/// <param name="Request">The binding of the exact request, <see cref="ServiceAssertion.Request"/>.</param>
/// <param name="Jti">The assertion id; the verifier owns replay.</param>
/// <param name="ExpiresAt">At most 60 seconds after signing.</param>
/// <param name="Subject">Optional subject identifier (payload <c>sub</c>), for example a brain id.</param>
/// <param name="Purpose">Optional purpose tag (payload <c>purpose</c>).</param>
public sealed record ServiceAssertionClaims(
    string Issuer,
    string Audience,
    string Operation,
    string Request,
    string Jti,
    DateTimeOffset ExpiresAt,
    string? Subject = null,
    string? Purpose = null);

/// <summary>
/// A service assertion: a compact JWS (ES256, P-256, IEEE P1363 signature) a trusted issuer sends as
/// <c>Authorization: Service &lt;token&gt;</c> when it calls an install's edge on its own behalf. Header
/// {"alg":"ES256","typ":"service","kid":&lt;issuer&gt;}; payload {"iss","aud","exp","jti","op","req"}. The <c>typ</c> keeps it apart
/// from a sign-in ticket in both directions. It lives at most 60 seconds, names one operation, binds the request's method, path,
/// query and body, and is accepted once; the verifier owns replay.
/// </summary>
public static partial class ServiceAssertion
{
    /// <summary>The Authorization scheme that carries an assertion.</summary>
    public const string Scheme = "Service";

    /// <summary>The header <c>typ</c> of an assertion.</summary>
    public const string Type = "service";

    /// <summary>The longest assertion lifetime in seconds.</summary>
    public const int LifetimeSeconds = 60;

    /// <summary>The longest assertion accepted, in characters.</summary>
    public const int MaxTokenLength = 4096;

    /// <summary>
    /// The binding of one request: base64url SHA-256 over the upper-case method, the path with its query, and the SHA-256 of the
    /// body, separated by line feeds.
    /// </summary>
    public static string Request(string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var text = method.ToUpperInvariant() + "\n" + pathAndQuery + "\n" + Convert.ToHexStringLower(SHA256.HashData(body));
        return CompactJws.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Signs <paramref name="claims"/>; throws <see cref="ArgumentException"/> for claims a verifier would refuse.</summary>
    public static string Sign(ServiceAssertionClaims claims, ECDsa privateKey, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(clock);
        if (!Valid(claims, clock.GetUtcNow()))
        { throw new ArgumentException("An assertion needs an operation, a request binding and a lifetime of at most 60 seconds.", nameof(claims)); }
        return CompactJws.Sign(
            JsonSerializer.SerializeToUtf8Bytes(new ServiceAssertionHeader("ES256", Type, claims.Issuer), ServiceAssertionJsonContext.Default.ServiceAssertionHeader),
            JsonSerializer.SerializeToUtf8Bytes(new ServiceAssertionPayload(claims.Issuer, claims.Audience, claims.ExpiresAt.ToUnixTimeSeconds(), claims.Jti,
                claims.Operation, claims.Request, claims.Subject, claims.Purpose), ServiceAssertionJsonContext.Default.ServiceAssertionPayload),
            privateKey);
    }

    /// <summary>
    /// The claims of an assertion a trusted issuer signed for this audience, still within its lifetime; null for anything else.
    /// <paramref name="keyOf"/> answers the issuer's key and expected audience by issuer name, or null for an unknown issuer. The
    /// caller still checks the operation, the request binding and replay.
    /// </summary>
    public static ServiceAssertionClaims? Verify(string? token, Func<string, (ECDsa Key, string Audience)?> keyOf, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(clock);
        if (CompactJws.Split(token, MaxTokenLength) is not { } parts) { return null; }
        try
        {
            var header = JsonSerializer.Deserialize(CompactJws.Decode(parts[0]), ServiceAssertionJsonContext.Default.ServiceAssertionHeader);
            if (header is not { Alg: "ES256", Typ: Type } || string.IsNullOrEmpty(header.Kid)) { return null; }
            if (keyOf(header.Kid) is not { } trusted) { return null; }
            if (!CompactJws.SignatureHolds(parts, trusted.Key)) { return null; }
            var payload = JsonSerializer.Deserialize(CompactJws.Decode(parts[1]), ServiceAssertionJsonContext.Default.ServiceAssertionPayload);
            if (payload is null || payload.Iss != header.Kid || payload.Aud is not { } audience || audience != trusted.Audience ||
                payload.Exp is < 0 or > 253_402_300_799) { return null; }
            var claims = new ServiceAssertionClaims(header.Kid, audience, payload.Op ?? "", payload.Req ?? "", payload.Jti ?? "",
                DateTimeOffset.FromUnixTimeSeconds(payload.Exp), payload.Sub, payload.Purpose);
            return Valid(claims, clock.GetUtcNow()) ? claims : null;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
        catch (CryptographicException) { return null; }
    }

    private static bool Valid(ServiceAssertionClaims claims, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(claims.Issuer) && claims.Issuer.Length <= 100 &&
        !string.IsNullOrWhiteSpace(claims.Audience) && claims.Audience.Length <= 500 &&
        claims.Operation is { Length: > 0 } && OperationName().IsMatch(claims.Operation) &&
        claims.Request is { Length: 43 } &&
        !string.IsNullOrWhiteSpace(claims.Jti) && claims.Jti.Length <= 128 &&
        claims.ExpiresAt > now && claims.ExpiresAt <= now.AddSeconds(LifetimeSeconds) &&
        (claims.Subject is null || (!string.IsNullOrWhiteSpace(claims.Subject) && claims.Subject.Length <= 200)) &&
        (claims.Purpose is null || (claims.Purpose is { Length: > 0 } && OperationName().IsMatch(claims.Purpose)));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex OperationName();
}

internal sealed record ServiceAssertionHeader(string? Alg, string? Typ, string? Kid);

internal sealed record ServiceAssertionPayload(
    string? Iss,
    string? Aud,
    long Exp,
    string? Jti,
    string? Op,
    string? Req,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sub = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Purpose = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceAssertionHeader))]
[JsonSerializable(typeof(ServiceAssertionPayload))]
internal sealed partial class ServiceAssertionJsonContext : JsonSerializerContext;
