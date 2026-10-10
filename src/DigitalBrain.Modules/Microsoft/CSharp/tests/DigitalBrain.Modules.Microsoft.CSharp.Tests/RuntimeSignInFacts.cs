using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Microsoft.CSharp.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class RuntimeSignInFacts
{
    [Fact(Timeout = 60_000)]
    public async Task SignInTouchesTheBrainRuntimeSoArmedBehaviorsAreAlive()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await brain.Grains.GetGrain<IBrainPrograms>(BrainRuntimeKeys.For("brain")).Assign("brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        host.Sessions.Clear();

        await SignInAsync(brain, ct);

        await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Single(host.Sessions);
    }

    [Fact(Timeout = 60_000)]
    public async Task SignInToABrainWithoutBehaviorsStartsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await SignInAsync(brain, ct);

        Assert.DoesNotContain(host.Requests, FakeSessionHost.IsStart);
    }

    // As the identity edge does: the handlers run as the member who just signed in.
    private static async Task SignInAsync(ModuleBrain brain, CancellationToken ct)
    {
        var member = new Member { PrincipalId = "acc", AccountId = "acc", BrainId = "brain", Role = MemberRole.Owner, DisplayName = "Acc" };
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = member.PrincipalId,
            AccountId = member.AccountId,
            BrainId = member.BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            await brain.AuthorizeCallerAsync();
            foreach (var handler in brain.SiloServices.GetServices<ISignInHandler>()) { await handler.SignedIn(member, ct); }
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }
}
