using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Circle.Server.Tests;

// D-02, D-09, T-10-01, T-10-03, T-10-20: a code and a P-256 key buy one machine a circle credential.
public sealed class EnrollmentFacts(CircleServerHost host)
{
    [Fact(Timeout = 120_000)]
    public async Task AValidCodeBindsTheMachineAndARepeatKeepsItsCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var machine = server.NewMachine();
        var code = server.Invite("circle-7", pending: true);

        var enrolled = await machine.EnrollOk(code);
        Assert.Equal(new CircleInfo("circle-7", "Friends", "member"), enrolled.Circle);
        Assert.True(enrolled.PendingAccount);
        Assert.Equal(CircleRoutes.Journal, enrolled.Endpoints.Journal);
        Assert.InRange(enrolled.AccessExpiresAt - server.Clock.GetUtcNow(), TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.InRange(enrolled.RefreshExpiresAt - server.Clock.GetUtcNow(), TimeSpan.FromDays(29), TimeSpan.FromDays(30));

        // The same machine and key again: the same credential, fresh tokens, and the earlier ones are dead.
        var again = await machine.EnrollOk(code);
        Assert.Equal(enrolled.MachineCredentialId, again.MachineCredentialId);
        Assert.NotEqual(enrolled.AccessToken, again.AccessToken);
        using var old = await machine.Send(HttpMethod.Get, CircleRoutes.Status, enrolled.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var fresh = await machine.Send(HttpMethod.Get, CircleRoutes.Status, again.AccessToken);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task OtherKeysExpiredAndUnknownCodesGetOnlyTheirFailureClass()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var machine = server.NewMachine();
        using var thief = server.NewMachine();
        var code = server.Invite();
        _ = await machine.EnrollOk(code);

        await AssertKind(await thief.Enroll(code), HttpStatusCode.Conflict, "rejected");
        await AssertKind(await machine.Enroll(server.Invite(expired: true)), HttpStatusCode.Gone, "expired");
        await AssertKind(await machine.Enroll("ABCD-EFGH-JKMN"), HttpStatusCode.Conflict, "rejected");
    }

    [Fact(Timeout = 120_000)]
    public async Task MalformedBodiesAndKeysThatAreNotP256AreRefusedWithFixedText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var machine = server.NewMachine();
        var code = server.Invite();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        foreach (var spki in new[] { "not base64 !", System.Buffers.Text.Base64Url.EncodeToString(p384.ExportSubjectPublicKeyInfo()),
            System.Buffers.Text.Base64Url.EncodeToString(rsa.ExportSubjectPublicKeyInfo()), "" })
        {
            using var response = await machine.Enroll(code, spki);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Malformed request.", await response.Content.ReadAsStringAsync(ct));
        }
        using var garbage = await server.Brain.HttpClient.PostAsync(CircleRoutes.Enroll, new StringContent("{nope", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        using var huge = await server.Brain.HttpClient.PostAsync(CircleRoutes.Enroll,
            new StringContent("{\"code\":\"" + new string('a', 9000) + "\"}", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);

        // None of those spent the code.
        _ = await machine.EnrollOk(code);
    }

    [Trait("host", "own")]
    [Fact(Timeout = 120_000)]
    public async Task TheEleventhAttemptFromOneAddressWithinTenMinutesIsRateLimited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await CircleServer.StartAsync(ct, perAddress: new CircleServerOptions().EnrollPerAddress);
        Assert.Equal(10, new CircleServerOptions().EnrollPerAddress);
        using var machine = server.NewMachine();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await AssertKind(await machine.Enroll("ABCD-EFGH-JKMN"), HttpStatusCode.Conflict, "rejected");
        }
        await AssertKind(await machine.Enroll(server.Invite()), HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Trait("host", "own")]
    [Fact(Timeout = 120_000)]
    public async Task TheThirtyFirstEnrollmentIntoOneCircleWithinAnHourIsRateLimited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await CircleServer.StartAsync(ct, perCircle: new CircleServerOptions().EnrollPerCircle);
        Assert.Equal(30, new CircleServerOptions().EnrollPerCircle);
        for (var index = 0; index < 30; index++)
        {
            using var each = server.NewMachine($"m-{index}");
            _ = await each.EnrollOk(server.Invite("busy"));
        }
        using var late = server.NewMachine("m-late");
        await AssertKind(await late.Enroll(server.Invite("busy")), HttpStatusCode.TooManyRequests, "rate_limited");
        using var elsewhere = server.NewMachine("m-other");
        _ = await elsewhere.EnrollOk(server.Invite("quiet"));
    }

    private static async Task AssertKind(HttpResponseMessage response, HttpStatusCode status, string kind)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal($"{{\"kind\":\"{kind}\"}}", await response.Content.ReadAsStringAsync());
        }
    }
}
