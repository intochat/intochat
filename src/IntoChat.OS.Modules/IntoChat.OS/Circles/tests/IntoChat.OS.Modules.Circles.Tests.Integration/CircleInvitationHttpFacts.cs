using System.Net;
using IntoChat.OS.Connections.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using static IntoChat.OS.Modules.Circles.Tests.CircleBrainRooms;

namespace IntoChat.OS.Modules.Circles.Tests;

public sealed class CircleInvitationHttpFacts(CirclesHost host)
{
    [Fact]
    public async Task OnlyAnInvitedPersonCanAcceptBeforeTheyAreACircleMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        var bob = await host.PersonAsync("bob", ct);
        var outsider = await host.PersonAsync("outsider", ct);
        var room = await Create(brain, alice);
        await alice.In(room, () => Circle(brain, room).Invite(bob.AccountId));
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        Assert.Null(await identity.Membership(room, bob.PrincipalId));
        using var anonymous = await brain.HttpClient.PostAsync($"/circle-invitations/{room}/accept", null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var refused = await outsider.Http.PostAsync($"/circle-invitations/{room}/accept", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var accepted = await bob.Http.PostAsync($"/circle-invitations/{room}/accept", null, ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotNull(await identity.Membership(room, bob.PrincipalId));
        Assert.Contains(bob.AccountId, (await alice.In(room, () => Circle(brain, room).State())).Members);
    }
}
