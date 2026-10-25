using System.Net;
using System.Net.Http.Json;

namespace IntoChat.Cloud.Circle.Server.Tests;

// D-09, T-10-02: refresh tokens rotate on every use; replaying a rotated one revokes the whole credential.
public sealed class CredentialRotationFacts(CircleServerHost host)
{
    [Fact(Timeout = 120_000)]
    public async Task RefreshRotatesAndReplayingTheOldTokenRevokesTheCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = host.Server;
        using var machine = server.NewMachine();
        var first = await machine.EnrollOk(server.Invite());

        using var rotated = await machine.Refresh(first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync(CircleJsonContext.Default.EnrollResponse, ct))!;
        Assert.Equal(first.MachineCredentialId, second.MachineCredentialId);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        using (var status = await machine.Send(HttpMethod.Get, CircleRoutes.Status, second.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.False((await status.Content.ReadFromJsonAsync<CircleStatusResponse>(ct))!.Revoked);
        }

        // The old refresh token, even with a valid proof from the right key: 401, and everything is revoked.
        using var replay = await machine.Refresh(first.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var current = await machine.Refresh(second.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
        using (var leave = await machine.Send(HttpMethod.Post, CircleRoutes.Leave, second.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, leave.StatusCode);
        }
        using var revoked = await machine.Send(HttpMethod.Get, CircleRoutes.Status, second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.True((await revoked.Content.ReadFromJsonAsync<CircleStatusResponse>(ct))!.Revoked);
    }

    [Trait("host", "own")]
    [Fact(Timeout = 120_000)]
    public async Task RefreshNeedsAProofFromTheBoundKeyAndAnUnexpiredToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await CircleServer.StartAsync(ct);
        using var machine = server.NewMachine();
        using var stranger = server.NewMachine("other");
        var enrolled = await machine.EnrollOk(server.Invite());

        // A thief holding the refresh token but not the key neither rotates nor revokes it.
        using var stolen = await machine.Refresh(enrolled.RefreshToken,
            await machine.Proof(HttpMethod.Post, CircleRoutes.Refresh, enrolled.RefreshToken, key: stranger.Key));
        Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
        using var garbage = await machine.Refresh("x");
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);

        // Access tokens live 15 minutes.
        server.Clock.Advance(TimeSpan.FromMinutes(16));
        using var expired = await machine.Send(HttpMethod.Get, CircleRoutes.Status, enrolled.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var renewed = await machine.Refresh(enrolled.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var next = (await renewed.Content.ReadFromJsonAsync(CircleJsonContext.Default.EnrollResponse, ct))!;

        // Refresh tokens live 30 days.
        server.Clock.Advance(TimeSpan.FromDays(31));
        using var stale = await machine.Refresh(next.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }
}
