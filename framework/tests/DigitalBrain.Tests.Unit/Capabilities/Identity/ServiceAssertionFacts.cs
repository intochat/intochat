using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Capabilities.Identity.Tests.Unit;

// A service assertion: the trusted issuer's signed word for one HTTP request it makes to an install. It is bound to the
// install (audience), the operation and the exact request, lives at most 60 seconds, and is never a sign-in ticket.
public sealed class ServiceAssertionFacts
{
    private const string Audience = "https://os.example";
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Request = ServiceAssertion.Request("POST", "/devices/enrollment/redeem", "{\"code\":\"x\"}"u8);

    private static ServiceAssertionClaims Claims(DateTimeOffset? expires = null, string operation = "devices.enrollment.redeem", string? request = null) =>
        new("cloud", Audience, operation, request ?? Request, "jti-1", expires ?? Now.AddSeconds(30));

    private static Func<string, (ECDsa Key, string Audience)?> Trust(ECDsa key) => issuer => issuer == "cloud" ? (key, Audience) : null;

    [Fact]
    public void A_signed_assertion_verifies_for_its_audience()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = ServiceAssertion.Sign(Claims(), key, new FixedTime(Now));

        var verified = ServiceAssertion.Verify(token, Trust(key), new FixedTime(Now));

        Assert.Equal(Claims(), verified);
    }

    [Fact]
    public void Another_key_audience_issuer_or_an_expired_assertion_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = ServiceAssertion.Sign(Claims(), key, new FixedTime(Now));

        Assert.Null(ServiceAssertion.Verify(token, _ => (other, Audience), new FixedTime(Now)));
        Assert.Null(ServiceAssertion.Verify(token, _ => (key, "https://elsewhere.example"), new FixedTime(Now)));
        Assert.Null(ServiceAssertion.Verify(token, _ => null, new FixedTime(Now)));
        Assert.Null(ServiceAssertion.Verify(token, Trust(key), new FixedTime(Now.AddSeconds(30))));
        Assert.Null(ServiceAssertion.Verify(null, Trust(key), new FixedTime(Now)));
        Assert.Null(ServiceAssertion.Verify(new string('a', ServiceAssertion.MaxTokenLength + 1), Trust(key), new FixedTime(Now)));
    }

    [Fact]
    public void A_tampered_payload_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parts = ServiceAssertion.Sign(Claims(), key, new FixedTime(Now)).Split('.');
        var forged = ServiceAssertion.Sign(Claims(operation: "devices.enrollment.list"), key, new FixedTime(Now)).Split('.');

        Assert.Null(ServiceAssertion.Verify(parts[0] + "." + forged[1] + "." + parts[2], Trust(key), new FixedTime(Now)));
    }

    [Fact]
    public void Lifetime_operation_and_request_are_checked_at_signing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal(60, ServiceAssertion.LifetimeSeconds);
        Assert.Throws<ArgumentException>(() => ServiceAssertion.Sign(Claims(Now.AddSeconds(61)), key, new FixedTime(Now)));
        Assert.Throws<ArgumentException>(() => ServiceAssertion.Sign(Claims(Now), key, new FixedTime(Now)));
        Assert.Throws<ArgumentException>(() => ServiceAssertion.Sign(Claims(operation: "Not An Operation"), key, new FixedTime(Now)));
        Assert.Throws<ArgumentException>(() => ServiceAssertion.Sign(Claims(request: ""), key, new FixedTime(Now)));
    }

    [Fact]
    public void A_service_assertion_is_never_a_sign_in_ticket_and_the_reverse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var assertion = ServiceAssertion.Sign(Claims(), key, new FixedTime(Now));
        var ticket = ExternalSignInTicket.Sign(new ExternalSignInClaims("cloud", Audience, "ada@example.com", "Ada", true, "pwd", "jti-2", Now.AddSeconds(30)), key, new FixedTime(Now));

        Assert.Null(ExternalSignInTicket.Verify(assertion, Trust(key), new FixedTime(Now)));
        Assert.Null(ServiceAssertion.Verify(ticket, Trust(key), new FixedTime(Now)));
    }

    [Fact]
    public void The_request_binding_covers_method_path_query_and_body()
    {
        var bound = ServiceAssertion.Request("GET", "/devices/enrollment?brain=ada", []);

        Assert.NotEqual(bound, ServiceAssertion.Request("POST", "/devices/enrollment?brain=ada", []));
        Assert.NotEqual(bound, ServiceAssertion.Request("GET", "/devices/enrollment?brain=eve", []));
        Assert.NotEqual(bound, ServiceAssertion.Request("GET", "/devices/enrollment?brain=ada", "x"u8));
        Assert.Equal(bound, ServiceAssertion.Request("get", "/devices/enrollment?brain=ada", []));
    }

    [Fact]
    public void The_header_names_its_own_type()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = ServiceAssertion.Sign(Claims(), key, new FixedTime(Now)).Split('.')[0];

        Assert.Equal("""{"alg":"ES256","typ":"service","kid":"cloud"}""", Encoding.UTF8.GetString(Decode(header)));
    }

    [Fact]
    public void Optional_subject_and_purpose_round_trip_and_are_omitted_when_null()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var scoped = new ServiceAssertionClaims(
            "cloud",
            Audience,
            "circle-compute-dispatch",
            Request,
            "jti-scoped",
            Now.AddSeconds(30),
            Subject: "brain-ada",
            Purpose: "circle-compute-dispatch");

        var token = ServiceAssertion.Sign(scoped, key, new FixedTime(Now));
        var verified = ServiceAssertion.Verify(token, Trust(key), new FixedTime(Now));
        Assert.Equal(scoped, verified);

        var payloadJson = Encoding.UTF8.GetString(Decode(token.Split('.')[1]));
        Assert.Contains("\"sub\":\"brain-ada\"", payloadJson, StringComparison.Ordinal);
        Assert.Contains("\"purpose\":\"circle-compute-dispatch\"", payloadJson, StringComparison.Ordinal);

        var plainToken = ServiceAssertion.Sign(Claims(), key, new FixedTime(Now));
        var plainPayload = Encoding.UTF8.GetString(Decode(plainToken.Split('.')[1]));
        Assert.DoesNotContain("\"sub\"", plainPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"purpose\"", plainPayload, StringComparison.Ordinal);
    }

    private static byte[] Decode(string value)
        => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

// A guest ticket: the trusted issuer's word that a visitor may try this install as a guest for a bounded time.
public sealed class GuestTicketFacts
{
    private const string Audience = "https://os.example";
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static GuestTicketClaims Claims(DateTimeOffset? expires = null, DateTimeOffset? until = null, string session = "s3ss10n") =>
        new("cloud", Audience, session, "jti-1", expires ?? Now.AddSeconds(30), until ?? Now.AddHours(1));

    private static Func<string, (ECDsa Key, string Audience)?> Trust(ECDsa key) => issuer => issuer == "cloud" ? (key, Audience) : null;

    [Fact]
    public void A_signed_ticket_verifies_for_its_audience()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = GuestTicket.Sign(Claims(), key, new FixedTime(Now));

        Assert.Equal(Claims(), GuestTicket.Verify(token, Trust(key), new FixedTime(Now)));
    }

    [Fact]
    public void Expired_foreign_or_overlong_tickets_are_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = GuestTicket.Sign(Claims(), key, new FixedTime(Now));

        Assert.Null(GuestTicket.Verify(token, _ => (other, Audience), new FixedTime(Now)));
        Assert.Null(GuestTicket.Verify(token, _ => (key, "https://elsewhere.example"), new FixedTime(Now)));
        Assert.Null(GuestTicket.Verify(token, Trust(key), new FixedTime(Now.AddSeconds(30))));
        Assert.Throws<ArgumentException>(() => GuestTicket.Sign(Claims(until: Now.Add(GuestTicket.LongestStay).AddSeconds(1)), key, new FixedTime(Now)));
        Assert.Throws<ArgumentException>(() => GuestTicket.Sign(Claims(expires: Now.AddSeconds(61)), key, new FixedTime(Now)));
        Assert.Throws<ArgumentException>(() => GuestTicket.Sign(Claims(session: "../etc"), key, new FixedTime(Now)));
    }

    [Fact]
    public void A_guest_ticket_is_neither_a_sign_in_ticket_nor_a_service_assertion()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var guest = GuestTicket.Sign(Claims(), key, new FixedTime(Now));

        Assert.Null(ExternalSignInTicket.Verify(guest, Trust(key), new FixedTime(Now)));
        Assert.Null(ServiceAssertion.Verify(guest, Trust(key), new FixedTime(Now)));
        var assertion = ServiceAssertion.Sign(new ServiceAssertionClaims("cloud", Audience, "guest", ServiceAssertion.Request("GET", "/", []), "jti-2", Now.AddSeconds(30)), key, new FixedTime(Now));
        Assert.Null(GuestTicket.Verify(assertion, Trust(key), new FixedTime(Now)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
