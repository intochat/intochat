using IntoChat.OS.Connections.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Connections.Tests.Unit;

// D-18: the Framework no longer registers what moved to Connections; AddIntoChatOSConnections must.
public sealed class ConnectionsHostingFacts
{
    [Fact]
    public void TheConnectionsHostingRegistersRedactionMembershipBindingsAndConnectorTypes()
    {
        var services = new ServiceCollection().AddIntoChatOSConnections();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ISecretRedactor) && descriptor.ImplementationType == typeof(SecretRedactor));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBrainMembership) && descriptor.ImplementationType == typeof(IdentityBrainMembership));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ICallBindings) && descriptor.ImplementationType == typeof(ConnectionCallBindings));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IConnectionBindings) && descriptor.ImplementationType == typeof(NoConnectionBindings));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IModuleTypeSource));
    }

    [Fact]
    public void AHostsOwnConnectionBindingsAreKept()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindings, Bound>();
        services.AddIntoChatOSConnections().AddIntoChatOSConnections();
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IConnectionBindings));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IModuleTypeSource));
    }

    [Fact]
    public async Task OnlyAConnectionKeyOfAnotherBrainIsAskedAbout()
    {
        var bindings = new Bound();
        var call = new ConnectionCallBindings(bindings);
        var other = ConnectionKeys.For("other", "github", "one");
        var own = ConnectionKeys.For("mine", "github", "one");

        Assert.True(await call.IsBound(Request("mine", "IConnection/" + other), "mine", TestContext.Current.CancellationToken));
        Assert.False(await call.IsBound(Request("mine", "IConnection/" + own), "mine", TestContext.Current.CancellationToken));
        Assert.False(await call.IsBound(Request("mine", "INote/other/note/one"), "mine", TestContext.Current.CancellationToken));
        Assert.Equal([other], bindings.Asked);
    }

    private static CallRequest Request(string brain, string target) => new()
    {
        Caller = new CallerContext { PrincipalId = "person", AccountId = brain, BrainId = brain, Kind = CallerKind.App, AppId = "app", StampedBy = TrustedEdge.AuthenticatedHttp },
        TargetNeuron = target,
        Operation = "Read",
    };

    private sealed class Bound : IConnectionBindings
    {
        public List<string> Asked { get; } = [];

        public ValueTask<bool> IsBound(CallerContext caller, string connectionKey, CancellationToken cancellationToken = default)
        {
            Asked.Add(connectionKey);
            return ValueTask.FromResult(true);
        }
    }
}
