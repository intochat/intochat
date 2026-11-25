using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Client;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.Kernel.Composition.Kernel.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Entries;
using IntoChat.OS.Modules.Identity.Contracts.Integrations;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.External;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// The silo's default rule (SiloAccessCallFilter): a grain call with no stamped caller is refused, and an OS silo has no external
// callers at all: a call this silo process did not send is refused whatever caller it declares, neuron contracts included.
//
// "Inside" facts send through the silo's own grain factory with the ambient caller cleared. "Cluster client" facts open a
// real Orleans client over TCP to the harness's own gateway port (loopback, ephemeral, this process only), so the request
// context arrives serialized, exactly as a separate process would send it.
[Trait("Category", "AccessFindings")]
public sealed class SiloRefusesByDefaultFacts(SiloRefusesByDefaultFacts.Silo silo) : IClassFixture<SiloRefusesByDefaultFacts.Silo>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string RuntimeAssembly = "DigitalBrain.Runtime.Orleans";

    // One representative call per grain interface of the review's table (06-ungated-grain-calls.md, section 6). One-way
    // methods (ISynapseHost.Nudge, Offer, CatchUp) take the same rule but return nothing to assert on.
    private static readonly Dictionary<string, Func<IGrainFactory, string, Task>> Calls = new(StringComparer.Ordinal)
    {
        ["INeuronHost.InspectState"] = (grains, brain) => Host(grains, brain).InspectState(Ct),
        ["INeuronHost.IsInterleaved"] = (grains, brain) => Host(grains, brain).IsInterleaved(ListFriends(brain)),
        ["ISynapseHost.Admit"] = (grains, brain) => Internal(grains, "DigitalBrain.Runtime.Orleans.Synapses.ISynapseHost", HostId(brain),
            "Admit", new NeuronId(new BrainId(brain), "probe", "p"), Array.Empty<string>()),
        ["ISynapseHost.EnsureBrainWide"] = (grains, brain) => Internal(grains, "DigitalBrain.Runtime.Orleans.Synapses.ISynapseHost", HostId(brain),
            "EnsureBrainWide", [Array.Empty<string>()]),
        ["IRemindable.ReceiveReminder"] = (grains, brain) => grains.GetGrain<IRemindable>(HostId(brain)).ReceiveReminder("probe", default),
        ["IPrincipalCredential.Hash"] = (grains, _) => grains.GetGrain<IPrincipalCredential>("owner").Hash(),
        ["IIdentityDirectory.BrainsOfAsync"] = (grains, _) => grains.GetGrain<IIdentityDirectory>("identity-directory").BrainsOfAsync("owner", Ct),
        ["IBrainEntries.List"] = (grains, brain) => grains.GetGrain<IBrainEntries>(brain).List(),
        ["IIntegration.Read"] = (grains, _) => grains.GetGrain<IIntegration>("integration/github").Read(),
        ["ITypeTable.List"] = (grains, brain) => grains.GetGrain<ITypeTable>(brain).List(),
        ["IInstanceDirectory.List"] = (grains, brain) => grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, FriendsDefaults.TypeName)).List(),
        ["IInstanceTypes.List"] = (grains, brain) => grains.GetGrain<IInstanceTypes>(brain).List(),
        ["ICompiledTypeTable.List"] = (grains, _) => grains.GetGrain<ICompiledTypeTable>(TypeTableKey.NoBrain).List(),
        ["INeuronHostCatalog.HostType"] = (grains, _) => grains.GetGrain<INeuronHostCatalog>(TypeTableKey.NoBrain).HostType(FriendsDefaults.TypeName),
        ["IStorageWriteGrants.Issue"] = (grains, brain) => Internal(grains, "DigitalBrain.Runtime.Orleans.Capabilities.Storage.IStorageWriteGrants", brain,
            "Issue", "connection", "requester", "fingerprint"),
        ["IExternalTicketReplay.Consume"] = (grains, _) => grains.GetGrain<IExternalTicketReplay>(IExternalTicketReplay.Key("issuer", "jti"))
            .Consume("jti", DateTimeOffset.UtcNow.AddMinutes(1)),
    };

    // What the allow-list names. Everything else in the table is refused inside without a caller, and everything is refused to a client.
    private static readonly string[] CallerlessInside = ["IRemindable.ReceiveReminder", "INeuronHostCatalog.HostType"];

    public static TheoryData<string> EveryCall => [.. Calls.Keys];

    // The three callers an outside process could declare for itself: the owner person, the owner's agent, an app acting for the owner.
    public static TheoryData<string, string> EveryCallUnderEveryStatedCaller =>
        [.. Calls.Keys.SelectMany(call => new[] { "person", "agent", "app" }.Select(stamp => (stamp, call)))];

    private static Caller Stated(EdgeAuthority edge, string brain, string stamp)
    {
        var owner = new Actor.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin);
        return stamp switch
        {
            "person" => edge.Person(owner.Principal, owner.Account, new BrainId(brain)),
            "agent" => edge.Agent(owner, new BrainId(brain)),
            "app" => edge.App("cli", owner, new BrainId(brain)),
            _ => throw new ArgumentOutOfRangeException(nameof(stamp), stamp, null),
        };
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task InsideTheSilo_ACallWithNoCaller_IsRefused_UnlessItIsAListedHostEntry(string name)
    {
        var host = silo.Host;
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        RequestContext.Clear();

        if (CallerlessInside.Contains(name))
        {
            await Calls[name](grains, host.Scope);
            return;
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calls[name](grains, host.Scope));
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task InsideTheSilo_TheSameCallUnderTheKernelOfTheBrain_IsServed(string name)
    {
        var host = silo.Host;
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();

        using (edge.EnterKernel(new BrainId(host.Scope)))
        {
            // The grain's own checks still apply (a synapse host asks who the subscriber is); the filter no longer refuses.
            var refused = await Record.ExceptionAsync(() => Calls[name](grains, host.Scope));
            Assert.False(refused is UnauthorizedAccessException { Message: var message } && IsFilterRefusal(message), refused?.Message);
        }
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task AClusterClient_WithNoCaller_IsRefusedEverywhere(string name)
    {
        var (host, client) = (silo.Host, silo.Client);
        RequestContext.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calls[name](client.Services.GetRequiredService<IClusterClient>(), host.Scope));
    }

    [Theory]
    [MemberData(nameof(EveryCallUnderEveryStatedCaller))]
    public async Task AClusterClient_ThatStatesItsOwnCaller_IsRefusedEverywhere(string stamp, string name)
    {
        var (host, client) = (silo.Host, silo.Client);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();

        // The stamp a client writes for itself. It travels serialized over the gateway, and the silo did not send the call.
        using (edge.Enter(Stated(edge, host.Scope, stamp)))
        {
            var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calls[name](client.Services.GetRequiredService<IClusterClient>(), host.Scope));
            Assert.True(IsFilterRefusal(refused.Message), refused.Message);
        }
    }

    // This harness opted in to a gateway (no OS host opens one), so the facts above have a real TCP client to refuse.
    [Fact]
    public async Task TheHarnessGateway_IsOpenOnlyBecauseItOptedIn()
    {
        Assert.Equal(silo.Host.GatewayPort, silo.Host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress?.Endpoint.Port);
        Assert.True(await IntoChat.OS.Tests.Integration.Cli.CliOverTheEdgeFacts.Listens(silo.Host.GatewayPort), "The opted-in gateway listens.");
    }

    [Fact]
    public async Task AClusterClient_CannotDeclareItselfTheKernel()
    {
        var (host, client) = (silo.Host, silo.Client);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();

        // The kernel stamp and the in-process scope token both cross the wire as copies; the silo trusts neither.
        using (edge.EnterKernel(new BrainId(host.Scope)))
        {
            var credential = client.Services.GetRequiredService<IClusterClient>().GetGrain<IPrincipalCredential>("owner");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(credential.Hash);
        }
    }

    // A neuron contract through a real client proxy, the way the command-line host used to call: the owner person, the owner's
    // agent or an app for the owner. The proxy's first routing question is already refused, and so is the call itself.
    [Theory]
    [InlineData("person")]
    [InlineData("agent")]
    [InlineData("app")]
    public async Task AClusterClient_ThatStatesItsOwnCaller_IsRefusedANeuronContract(string stamp)
    {
        var (host, client) = (silo.Host, silo.Client);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();

        using (edge.Enter(Stated(edge, host.Scope, stamp)))
        {
            var friends = SiloNeurons.Get<IFriends>(client.Services, FriendsDefaults.Id(host.Scope));
            var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(friends.List);
            Assert.True(IsFilterRefusal(refused.Message), refused.Message);
        }
    }

    // The same neuron contract call bound by hand, past the proxy's routing questions, straight to the host grain.
    [Theory]
    [InlineData("person")]
    [InlineData("agent")]
    [InlineData("app")]
    public async Task AClusterClient_ThatStatesItsOwnCaller_IsRefusedAtTheHostGrain(string stamp)
    {
        var (host, client) = (silo.Host, silo.Client);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();

        using (edge.Enter(Stated(edge, host.Scope, stamp)))
        {
            var grains = client.Services.GetRequiredService<IClusterClient>();
            var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Host(grains, host.Scope).Invoke(ListFriends(host.Scope), Ct));
            Assert.True(IsFilterRefusal(refused.Message), refused.Message);
        }
    }

    // The allow-listed reminder entry, driven by the Orleans reminder service itself rather than by a fact: a real
    // registration falls due and its tick, which carries no caller, reaches the host grain past the default rule.
    [Fact]
    public async Task ARealReminderTick_SentByTheReminderService_StillReachesItsNeuron()
    {
        var arrived = new ReminderArrivals();
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Configure<Orleans.Hosting.ReminderOptions>(options => options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100));
            // Registered last, so it runs inside SiloAccessCallFilter: it sees only what the rule admitted.
            builder.Services.AddSingleton<IIncomingGrainCallFilter>(arrived);
        });
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        var reminder = SiloNeurons.Get<IntoChat.OS.Modules.Time.Reminders.IReminder>(
            host.Services, new NeuronId(new BrainId(host.Scope), "reminder", "default-rule"));

        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(host.Scope))))
        {
            await reminder.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
        }

        var tick = await arrived.First.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(host.Scope))))
        {
            await reminder.Stop();
        }

        Assert.True(tick.SentByThisSilo, "The reminder service's own call carries the silo's mark.");
        Assert.False(tick.HadCaller, "A reminder tick carries no caller; the host grain enters the scheduler itself.");
    }

    private sealed class ReminderArrivals : IIncomingGrainCallFilter
    {
        public TaskCompletionSource<(bool SentByThisSilo, bool HadCaller)> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.InterfaceMethod.DeclaringType == typeof(IRemindable))
            {
                First.TrySetResult((SiloOrigin.IsCurrent, RequestContext.Get("db.access.caller") is not null));
            }
            return context.Invoke();
        }
    }

    private static bool IsFilterRefusal(string message)
        => message.Contains("carried no caller provenance", StringComparison.Ordinal)
            || message.Contains("was not sent by this silo process", StringComparison.Ordinal);

    // One silo and one cluster client for the class: every fact only asks whether a call is admitted.
    public sealed class Silo : IAsyncLifetime
    {
        internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host { get; private set; } = null!;
        internal IHost Client { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            // No OS host opens a gateway; this one does, so the facts can prove that nothing is admitted through one.
            Host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(CancellationToken.None, openGateway: true);
            Client = new HostBuilder()
                .UseOrleansClient(client =>
                {
                    client.UseLocalhostClustering(Host.GatewayPort);
                    client.Services.AddIntoChatOSClient();
                    OrleansAotResponses.Register(client.Services);
                })
                .Build();
            await Client.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            Client.Dispose();
            await Host.DisposeAsync();
        }
    }

    private static GrainId HostId(string brain) => KernelTestHooks.HostOf(FriendsDefaults.Id(brain));

    private static INeuronHost Host(IGrainFactory grains, string brain) => grains.GetGrain<INeuronHost>(HostId(brain));

    private static RuntimeInvocation ListFriends(string brain)
        => new(string.Empty, typeof(IFriends).FullName!, FriendsDefaults.Id(brain).ToString(), nameof(IFriends.List), Array.Empty<JsonElement>());

    // The framework keeps these grain interfaces internal; a client binds them by name all the same.
    private static Task Internal(IGrainFactory grains, string contract, object key, string method, params object[] arguments)
    {
        var type = Assembly.Load(RuntimeAssembly).GetType(contract, throwOnError: true)!;
        var grain = key is GrainId id
            ? grains.GetGrain(id).AsReference(type)
            : grains.GetGrain(type, (string)key);
        return (Task)type.GetMethod(method)!.Invoke(grain, arguments)!;
    }
}
