using System.Net;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Two real OS silos in one cluster (loopback, ephemeral ports, this process only): the second host joins the first one's
// membership the way the Azure install's second container app (the Cloud app, or a new revision of the OS app while the old
// one still runs) joins the shared cluster.
[Trait("Category", "AccessFindings")]
public sealed class TwoSiloClusterFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // How long the first silo may take to record the refused second silo as dead once it is told to. Generous: a loaded CI
    // runner is slow, and nothing here measures how fast membership converges.
    private static readonly TimeSpan MembershipBudget = TimeSpan.FromSeconds(60);

    private static Task<Gate20EdgeApprovalWallFacts.EdgeKernelHarness> StartPrimaryAsync()
        => Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);

    // The second silo failed its start after it had written its membership row; the harness's best-effort stop of a failed
    // host does not mark that row dead. Until the first silo's probes find it gone, the first silo's own stop waits a full
    // response timeout on a call to it (call cancellation goes to every silo not dead). Declare it dead and wait until the
    // first silo's membership shows that, so the first silo stops at once with its default timeouts.
    private static async Task ForgetTheRefusedSilo(Gate20EdgeApprovalWallFacts.EdgeKernelHarness a)
    {
        var membership = a.Services.GetRequiredService<IClusterMembershipService>();
        var self = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(MembershipBudget);
        while (membership.CurrentSnapshot.Members.Values.Where(member => !member.SiloAddress.Equals(self) && member.Status != SiloStatus.Dead).ToArray() is { Length: > 0 } others)
        {
            foreach (var other in others) { await membership.TryKill(other.SiloAddress); }
            await Task.Delay(50, limit.Token);
        }
    }

    // Why SingleSiloGuard exists, with the guard taken out of the second silo. The first silo's startup activates the
    // identity grains; the second silo's own startup (a module caller, the integration seeder) calls them on the first
    // silo, which refuses the call because the origin mark of the second process does not count there.
    [Fact]
    public async Task WithoutTheGuard_ASecondSilo_CannotStart_BecauseItsInternalCallsToTheFirstAreRefused()
    {
        await using var a = await StartPrimaryAsync();
        var primary = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.Endpoint;

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var b = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
            {
                Join(builder, primary);
                foreach (var guard in builder.Services.Where(descriptor => descriptor.ImplementationType == typeof(SingleSiloGuard)).ToArray())
                {
                    builder.Services.Remove(guard);
                }
            });
        });
        await ForgetTheRefusedSilo(a);

        Assert.Contains(Chain(error), e => e is UnauthorizedAccessException && e.Message.Contains("cannot claim actor kind", StringComparison.Ordinal));
    }

    // The guard: the second silo refuses to become active, with the reason, before Orleans places a grain on it or any
    // startup task runs; the first silo keeps serving alone.
    [Fact]
    public async Task ASecondSilo_RefusesToJoin_AndTheFirstKeepsServing()
    {
        await using var a = await StartPrimaryAsync();
        var primary = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.Endpoint;

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var b = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder => Join(builder, primary));
        });
        await ForgetTheRefusedSilo(a);

        var refusal = Assert.Single(Chain(error), e => e is InvalidOperationException && e.Message.Contains("must be the only silo of its cluster", StringComparison.Ordinal));
        Assert.Contains(primary.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Chain(error), e => e is UnauthorizedAccessException);

        var management = a.Services.GetRequiredService<IGrainFactory>().GetGrain<IManagementGrain>(0);
        Assert.Single(await management.GetHosts(true, Ct));
        var edge = a.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();
        using (edge.EnterKernel(new BrainId(a.Scope)))
        {
            Assert.Empty(await SiloNeurons.Get<IFriends>(a.Services, FriendsDefaults.Id(a.Scope)).List());
        }
    }

    // A silo alone in its cluster starts with the guard in place: the single-silo shape is unchanged.
    [Fact]
    public async Task ASiloAlone_StartsWithTheGuard()
    {
        await using var a = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        Assert.Contains(a.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(), participant => participant is SingleSiloGuard);
    }

    internal static void Join(WebApplicationBuilder builder, IPEndPoint primary)
    {
        var siloPort = int.Parse(builder.Configuration[DigitalBrain.Runtime.Orleans.TopologyContext.SiloPortKey]!, System.Globalization.CultureInfo.InvariantCulture);
        builder.UseOrleans(orleans => orleans.UseLocalhostClustering(siloPort, DigitalBrain.Runtime.Orleans.TopologyContext.NoGateway, primary));
        // The harness disposes a host whose start failed without stopping it; its silo connection still receives the first
        // silo's membership gossip and would log into a disposed Windows event log, which ends the test process.
        builder.Logging.ClearProviders();
    }

    internal static IEnumerable<Exception> Chain(Exception error)
    {
        var pending = new Stack<Exception>([error]);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is AggregateException aggregate) { foreach (var inner in aggregate.InnerExceptions) { pending.Push(inner); } }
            else if (current.InnerException is { } inner) { pending.Push(inner); }
        }
    }
}
