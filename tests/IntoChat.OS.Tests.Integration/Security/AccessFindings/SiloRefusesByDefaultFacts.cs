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

// The silo's default rule (SiloAccessCallFilter): a grain call with no stamped caller is refused; a call a cluster client
// sent reaches neuron contracts and the few routing entries its proxies need, nothing else, whatever caller it declares.
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

    // What the allow-lists name. Everything else in the table is refused in both positions.
    private static readonly string[] CallerlessInside = ["IRemindable.ReceiveReminder", "INeuronHostCatalog.HostType"];
    private static readonly string[] ServedToClients =
        ["INeuronHost.IsInterleaved", "ICompiledTypeTable.List", "INeuronHostCatalog.HostType"];

    public static TheoryData<string> EveryCall => [.. Calls.Keys];

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
    [MemberData(nameof(EveryCall))]
    public async Task AClusterClient_ThatDeclaresItselfTheOwner_ReachesOnlyTheRoutingEntries(string name)
    {
        var (host, client) = (silo.Host, silo.Client);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();

        // The stamp a client writes for itself: the owner person of the brain. It travels serialized over the gateway.
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(host.Scope))))
        {
            var grains = client.Services.GetRequiredService<IClusterClient>();
            if (ServedToClients.Contains(name))
            {
                // Served: whatever the grain answers (a probe invocation need not bind), the filter did not refuse it.
                var refused = await Record.ExceptionAsync(() => Calls[name](grains, host.Scope));
                Assert.False(refused is UnauthorizedAccessException { Message: var message } && IsFilterRefusal(message), refused?.Message);
                return;
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calls[name](grains, host.Scope));
        }
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

    [Fact]
    public async Task AClusterClient_ActingAsAnApp_StillRoutesANeuronCallThroughTheGate()
    {
        var (host, client) = (silo.Host, silo.Client);
        RequestContext.Clear();

        // The documented client stamp (ClientCaller.ActAs): the proxy resolves the host grain and asks the interleaving
        // policy, then the call reaches the stages. An app without a grant is refused there, by the gate, not by routing.
        using (ClientCaller.ActAs("cli", AccountSession.DefaultLogin, AccountSession.DefaultLogin, host.Scope))
        {
            var friends = SiloNeurons.Get<IFriends>(client.Services, FriendsDefaults.Id(host.Scope));
            var refused = await Record.ExceptionAsync(friends.List);
            Assert.False(refused is UnauthorizedAccessException { Message: var message } && IsFilterRefusal(message), refused?.Message);
        }
    }

    // OPEN (stated in the build report): the gateway has no authentication, so a cluster client that declares itself a
    // person of a brain is admitted to that brain's neuron contracts as that person. The filter cannot tell a first-party
    // front from a stranger; closing this needs a kernel-issued gateway credential (ClientCaller.cs). This fact pins the
    // behaviour so that the change that closes it has to turn it around.
    [Fact]
    public async Task Open_AClusterClient_ThatDeclaresItselfTheOwner_IsAdmittedToNeuronContracts()
    {
        var (host, client) = (silo.Host, silo.Client);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();

        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(host.Scope))))
        {
            var friends = SiloNeurons.Get<IFriends>(client.Services, FriendsDefaults.Id(host.Scope));
            Assert.Empty(await friends.List());
        }
    }

    private static bool IsFilterRefusal(string message)
        => message.Contains("carried no caller provenance", StringComparison.Ordinal)
            || message.Contains("served inside the silo only", StringComparison.Ordinal);

    // One silo and one cluster client for the class: every fact only asks whether a call is admitted.
    public sealed class Silo : IAsyncLifetime
    {
        internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host { get; private set; } = null!;
        internal IHost Client { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            Host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(CancellationToken.None);
            Client = new HostBuilder()
                .UseOrleansClient(client =>
                {
                    client.UseLocalhostClustering(Host.GatewayPort).AddIntoChatOS();
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
