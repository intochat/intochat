using System.Net;
using System.Text.Json;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Devices;

namespace IntoChat.OS.Tests.Integration.Modules.Devices;

// Owner decision D4: the OS owns device enrollment, and the cloud relay (its own cluster) asks the install's edge with a signed
// service assertion. Driven through the real route map, auth middleware, identity module and replay ledger of a real host.
[Trait("Category", "AccessFindings")]
public sealed class EnrollmentEdgeFacts(CloudEdgeHost os) : IClassFixture<CloudEdgeHost>
{
    private static byte[] Body(RedeemRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, EnrollmentEdgeJson.Default.RedeemRequest);

    private static async Task<T> Read<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        => JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), type)!;

    [Fact]
    public async Task TheCloudRedeemsAnOwnersCode_ThenListsAndChecksTheDevice_EachWithItsOwnAssertion()
    {
        var code = await os.MintAsync();
        var machine = "m-" + Guid.NewGuid().ToString("N")[..8];

        using var redeemed = await os.SendAssertedAsync(EnrollmentEdge.RedeemOperation, HttpMethod.Post, EnrollmentEdge.Redeem,
            Body(new RedeemRequest(code.Value, machine, "thumb-" + machine, "Ada's PC")));
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var outcome = (await Read(redeemed, EnrollmentEdgeJson.Default.RedeemResponse)).ToOutcome();
        Assert.Equal((EnrollmentResult.Accepted, os.Brain, "Ada's PC"), (outcome.Result, outcome.Brain, outcome.DeviceName));

        using var listed = await os.SendAssertedAsync(EnrollmentEdge.ListOperation, HttpMethod.Get, EnrollmentEdge.ListFor(os.Brain));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains(await Read(listed, EnrollmentEdgeJson.Default.EnrolledDeviceArray), device => device.MachineId == machine);

        using var live = await os.SendAssertedAsync(EnrollmentEdge.LiveOperation, HttpMethod.Get, EnrollmentEdge.LiveFor(os.Brain, machine, "thumb-" + machine));
        Assert.True((await Read(live, EnrollmentEdgeJson.Default.LiveResponse)).Live);

        using var otherKey = await os.SendAssertedAsync(EnrollmentEdge.LiveOperation, HttpMethod.Get, EnrollmentEdge.LiveFor(os.Brain, machine, "another-key"));
        Assert.False((await Read(otherKey, EnrollmentEdgeJson.Default.LiveResponse)).Live);
    }

    [Fact]
    public async Task AnUnknownCodeIsRejected_NotAnError()
    {
        using var response = await os.SendAssertedAsync(EnrollmentEdge.RedeemOperation, HttpMethod.Post, EnrollmentEdge.Redeem,
            Body(new RedeemRequest("AAAA-BBBB-CCCC", "m1", "t1", "PC")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EnrollmentResult.Rejected, (await Read(response, EnrollmentEdgeJson.Default.RedeemResponse)).ToOutcome().Result);
    }

    [Fact]
    public async Task WithoutAnAssertion_EveryRouteIsRefused()
    {
        using var redeem = await os.SendAsync(HttpMethod.Post, EnrollmentEdge.Redeem, Body(new RedeemRequest("AAAA-BBBB-CCCC", "m1", "t1", "PC")), null);
        using var list = await os.SendAsync(HttpMethod.Get, EnrollmentEdge.ListFor(os.Brain), null, null);
        using var live = await os.SendAsync(HttpMethod.Get, EnrollmentEdge.LiveFor(os.Brain, "m1", "t1"), null, null);
        using var bearer = await os.SendAsync(HttpMethod.Get, EnrollmentEdge.ListFor(os.Brain), null, "Bearer not-a-service");

        Assert.All([redeem, list, live, bearer], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task AnAssertionIsAdmittedOnce()
    {
        var target = EnrollmentEdge.ListFor(os.Brain);
        var assertion = os.Assert(EnrollmentEdge.ListOperation, HttpMethod.Get, target, []);

        using var first = await os.SendAsync(HttpMethod.Get, target, null, assertion);
        using var replay = await os.SendAsync(HttpMethod.Get, target, null, assertion);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task AnAssertionForOneRequest_AdmitsNoOther()
    {
        // Another brain, another machine, another body, another operation: each is refused.
        var forAda = os.Assert(EnrollmentEdge.ListOperation, HttpMethod.Get, EnrollmentEdge.ListFor(os.Brain), []);
        using var otherBrain = await os.SendAsync(HttpMethod.Get, EnrollmentEdge.ListFor("other-brain"), null, forAda);

        var forM1 = os.Assert(EnrollmentEdge.LiveOperation, HttpMethod.Get, EnrollmentEdge.LiveFor(os.Brain, "m1", "t1"), []);
        using var otherMachine = await os.SendAsync(HttpMethod.Get, EnrollmentEdge.LiveFor(os.Brain, "m2", "t1"), null, forM1);

        var body = Body(new RedeemRequest("AAAA-BBBB-CCCC", "m1", "t1", "PC"));
        var forBody = os.Assert(EnrollmentEdge.RedeemOperation, HttpMethod.Post, EnrollmentEdge.Redeem, body);
        using var otherBody = await os.SendAsync(HttpMethod.Post, EnrollmentEdge.Redeem, Body(new RedeemRequest("AAAA-BBBB-CCCC", "m9", "t9", "PC")), forBody);

        var listAsLive = os.Assert(EnrollmentEdge.ListOperation, HttpMethod.Get, EnrollmentEdge.LiveFor(os.Brain, "m1", "t1"), []);
        using var otherOperation = await os.SendAsync(HttpMethod.Get, EnrollmentEdge.LiveFor(os.Brain, "m1", "t1"), null, listAsLive);

        Assert.All([otherBrain, otherMachine, otherBody, otherOperation], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task AnIssuerNotTrustedForServices_AnotherAudience_AnExpiredAssertion_OrASignInTicket_IsRefused()
    {
        var target = EnrollmentEdge.ListFor(os.Brain);
        var signInOnly = os.Assert(EnrollmentEdge.ListOperation, HttpMethod.Get, target, [], issuer: CloudEdgeHost.SignInOnly, key: os.SignInKey);
        var forgedKey = os.Assert(EnrollmentEdge.ListOperation, HttpMethod.Get, target, [], key: os.SignInKey);
        var otherAudience = os.Assert(EnrollmentEdge.ListOperation, HttpMethod.Get, target, [], audience: "https://elsewhere.test");
        var expired = os.Assert(EnrollmentEdge.ListOperation, HttpMethod.Get, target, [], clock: new Fixed(DateTimeOffset.UtcNow.AddMinutes(-2)));
        var ticket = ServiceAssertion.Scheme + " " + ExternalSignInTicket.Sign(new ExternalSignInClaims(CloudEdgeHost.Cloud, CloudEdgeHost.Audience,
            "ada@example.com", "Ada", true, "pwd", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(30)), os.CloudKey, TimeProvider.System);

        foreach (var authorization in new[] { signInOnly, forgedKey, otherAudience, expired, ticket })
        {
            using var response = await os.SendAsync(HttpMethod.Get, target, null, authorization);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
