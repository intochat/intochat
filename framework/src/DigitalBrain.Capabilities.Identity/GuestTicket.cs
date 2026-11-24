using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DigitalBrain.Capabilities.Identity;

/// <summary>What an issuer vouches for when it sends an anonymous visitor to try an install as a guest.</summary>
/// <param name="Issuer">The issuer name (header <c>kid</c>, payload <c>iss</c>).</param>
/// <param name="Audience">The install origin the ticket is for.</param>
/// <param name="Session">The issuer's try session, 1-64 characters of letters, digits, '-' or '_'; with the issuer it names the guest.</param>
/// <param name="Jti">The ticket id; the verifier owns replay.</param>
/// <param name="ExpiresAt">At most 60 seconds after signing.</param>
/// <param name="Until">When the guest's stay ends, at most <see cref="GuestTicket.LongestStay"/> after signing.</param>
public sealed record GuestTicketClaims(string Issuer, string Audience, string Session, string Jti, DateTimeOffset ExpiresAt, DateTimeOffset Until);

/// <summary>
/// A guest ticket: a compact JWS (ES256) like the sign-in ticket, with header {"alg":"ES256","typ":"guest","kid":&lt;issuer&gt;}
/// and payload {"iss","aud","exp","jti","sid","until"}. It carries no person: the install makes a guest from the issuer and the
/// session, for the stay the ticket names. It lives at most 60 seconds and is redeemed once; the verifier owns replay.
/// </summary>
public static partial class GuestTicket
{
    /// <summary>The header <c>typ</c> of a guest ticket.</summary>
    public const string Type = "guest";

    /// <summary>The longest ticket lifetime in seconds.</summary>
    public const int LifetimeSeconds = 60;

    /// <summary>The longest ticket accepted, in characters.</summary>
    public const int MaxTokenLength = 4096;

    /// <summary>The longest stay a ticket may grant.</summary>
    public static readonly TimeSpan LongestStay = TimeSpan.FromHours(24);

    /// <summary>Signs <paramref name="claims"/>; throws <see cref="ArgumentException"/> for claims a verifier would refuse.</summary>
    public static string Sign(GuestTicketClaims claims, ECDsa privateKey, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(clock);
        if (!Valid(claims, clock.GetUtcNow()))
        { throw new ArgumentException("A guest ticket needs a session, a lifetime of at most 60 seconds and a stay of at most 24 hours.", nameof(claims)); }
        return CompactJws.Sign(
            JsonSerializer.SerializeToUtf8Bytes(new GuestTicketHeader("ES256", Type, claims.Issuer), GuestTicketJsonContext.Default.GuestTicketHeader),
            JsonSerializer.SerializeToUtf8Bytes(new GuestTicketPayload(claims.Issuer, claims.Audience, claims.ExpiresAt.ToUnixTimeSeconds(), claims.Jti,
                claims.Session, claims.Until.ToUnixTimeSeconds()), GuestTicketJsonContext.Default.GuestTicketPayload),
            privateKey);
    }

    /// <summary>
    /// The claims of a guest ticket a trusted issuer signed for this audience, still within its lifetime; null for anything else.
    /// <paramref name="keyOf"/> answers the issuer's key and expected audience by issuer name, or null for an unknown issuer.
    /// </summary>
    public static GuestTicketClaims? Verify(string? token, Func<string, (ECDsa Key, string Audience)?> keyOf, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(clock);
        if (CompactJws.Split(token, MaxTokenLength) is not { } parts) { return null; }
        try
        {
            var header = JsonSerializer.Deserialize(CompactJws.Decode(parts[0]), GuestTicketJsonContext.Default.GuestTicketHeader);
            if (header is not { Alg: "ES256", Typ: Type } || string.IsNullOrEmpty(header.Kid)) { return null; }
            if (keyOf(header.Kid) is not { } trusted) { return null; }
            if (!CompactJws.SignatureHolds(parts, trusted.Key)) { return null; }
            var payload = JsonSerializer.Deserialize(CompactJws.Decode(parts[1]), GuestTicketJsonContext.Default.GuestTicketPayload);
            if (payload is null || payload.Iss != header.Kid || payload.Aud is not { } audience || audience != trusted.Audience ||
                payload.Exp is < 0 or > 253_402_300_799 || payload.Until is < 0 or > 253_402_300_799) { return null; }
            var claims = new GuestTicketClaims(header.Kid, audience, payload.Sid ?? "", payload.Jti ?? "",
                DateTimeOffset.FromUnixTimeSeconds(payload.Exp), DateTimeOffset.FromUnixTimeSeconds(payload.Until));
            return Valid(claims, clock.GetUtcNow()) ? claims : null;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
        catch (CryptographicException) { return null; }
    }

    private static bool Valid(GuestTicketClaims claims, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(claims.Issuer) && claims.Issuer.Length <= 100 &&
        !string.IsNullOrWhiteSpace(claims.Audience) && claims.Audience.Length <= 500 &&
        claims.Session is { Length: > 0 } && SessionName().IsMatch(claims.Session) &&
        !string.IsNullOrWhiteSpace(claims.Jti) && claims.Jti.Length <= 128 &&
        claims.ExpiresAt > now && claims.ExpiresAt <= now.AddSeconds(LifetimeSeconds) &&
        claims.Until > now && claims.Until <= now.Add(LongestStay);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SessionName();
}

internal sealed record GuestTicketHeader(string? Alg, string? Typ, string? Kid);

internal sealed record GuestTicketPayload(string? Iss, string? Aud, long Exp, string? Jti, string? Sid, long Until);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuestTicketHeader))]
[JsonSerializable(typeof(GuestTicketPayload))]
internal sealed partial class GuestTicketJsonContext : JsonSerializerContext;
