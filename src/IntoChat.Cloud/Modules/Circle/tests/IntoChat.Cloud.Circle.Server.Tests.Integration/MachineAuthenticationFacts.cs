using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Circle.Server.Tests;

// D-09, T-10-01, T-10-02, T-10-18: circle module routes take a bearer token plus a proof from the bound key; nothing
// else (no owner cookie, no replay, no stale or misdirected proof) gets through.
public sealed class MachineAuthenticationFacts(CircleServerHost host)
{
    [Fact(Timeout = 120_000)]
    public async Task AValidBearerAndProofReachStatusWithTheMachineClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var machine = server.NewMachine("desk-1");
        var enrolled = await machine.EnrollOk(server.Invite("circle-9"));

        using var response = await machine.Send(HttpMethod.Get, CircleRoutes.Status, enrolled.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.Date);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        Assert.Equal(new CircleStatusResponse("circle-9", "Friends", "member", false, false, "desk-1"),
            await response.Content.ReadFromJsonAsync<CircleStatusResponse>(ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task MissingMisdirectedReplayedStaleAndForeignProofsAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var machine = server.NewMachine();
        using var stranger = server.NewMachine("other");
        var token = (await machine.EnrollOk(server.Invite())).AccessToken;
        var get = HttpMethod.Get;

        await Refused(machine.Send(get, CircleRoutes.Status, token, withProof: false));
        await Refused(machine.Send(get, CircleRoutes.Status, token, await machine.Proof(get, CircleRoutes.Store, token)));
        await Refused(machine.Send(get, CircleRoutes.Status, token, await machine.Proof(HttpMethod.Post, CircleRoutes.Status, token)));
        await Refused(machine.Send(get, CircleRoutes.Status, token, await machine.Proof(get, CircleRoutes.Status, token, key: stranger.Key)));
        await Refused(machine.Send(get, CircleRoutes.Status, token, await machine.Proof(get, CircleRoutes.Status, token, server.Clock.GetUtcNow().AddSeconds(-61))));
        await Refused(machine.Send(get, CircleRoutes.Status, token, await machine.Proof(get, CircleRoutes.Status, token, server.Clock.GetUtcNow().AddSeconds(61))));
        await Refused(machine.Send(get, CircleRoutes.Status, token, await machine.Proof(get, CircleRoutes.Status, token + "x")));
        await Refused(machine.Send(get, CircleRoutes.Status, "not-a-token", await machine.Proof(get, CircleRoutes.Status, "not-a-token")));

        var once = await machine.Proof(get, CircleRoutes.Status, token);
        using (var first = await machine.Send(get, CircleRoutes.Status, token, once)) { Assert.Equal(HttpStatusCode.OK, first.StatusCode); }
        await Refused(machine.Send(get, CircleRoutes.Status, token, once));

        // Skew inside the window is fine.
        using var skewed = await machine.Send(get, CircleRoutes.Status, token, await machine.Proof(get, CircleRoutes.Status, token, server.Clock.GetUtcNow().AddSeconds(-50)));
        Assert.Equal(HttpStatusCode.OK, skewed.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnOwnerSessionAloneNeverReachesACircleModuleRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        var owner = await server.Brain.PersonAsync("alice", ct);

        using var status = await owner.Http.GetAsync(CircleRoutes.Status, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        using var leave = await owner.Http.PostAsync(CircleRoutes.Leave, null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, leave.StatusCode);
        using var anonymous = await server.Brain.HttpClient.GetAsync(CircleRoutes.Status, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task APendingMachineIsForbiddenWhereAnApprovedMachineIsRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var pending = server.NewMachine("pending");
        using var approved = server.NewMachine("approved");
        var waiting = await pending.EnrollOk(server.Invite(pending: true));
        var member = await approved.EnrollOk(server.Invite());

        using var refused = await pending.Send(HttpMethod.Get, "/circle/v1/probe", waiting.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var allowed = await approved.Send(HttpMethod.Get, "/circle/v1/probe", member.AccessToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task LeaveRevokesTheCredentialAndStatusSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var machine = server.NewMachine();
        var enrolled = await machine.EnrollOk(server.Invite());

        using (var leave = await machine.Send(HttpMethod.Post, CircleRoutes.Leave, enrolled.AccessToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        }
        using (var again = await machine.Send(HttpMethod.Post, CircleRoutes.Leave, enrolled.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        }
        using var refresh = await machine.Refresh(enrolled.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        using var status = await machine.Send(HttpMethod.Get, CircleRoutes.Status, enrolled.AccessToken);
        Assert.True((await status.Content.ReadFromJsonAsync<CircleStatusResponse>(ct))!.Revoked);
    }

    private static async Task Refused(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Machine authentication failed.", await response.Content.ReadAsStringAsync());
    }
}
