using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Access;
using System.Reflection;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Capabilities;

/// <summary>Host configuration of the capability registry, read when the silo is configured.</summary>
public static class CapabilityHosting
{
    internal static IIdentityDirectoryPort WrapPort(IIdentityDirectoryPort port)
        => port is PlatformIdentityDirectoryPort ? port : new PlatformIdentityDirectoryPort(port);

    internal static IIdentitySessionPort WrapPort(IIdentitySessionPort port)
        => port is PlatformIdentitySessionPort ? port : new PlatformIdentitySessionPort(port);

    private static ICallerAmbient Callers(IServiceProvider services)
        => services.GetService<ICallerAmbient>() ?? OrleansCallerAmbient.Instance;

    internal static readonly Func<IServiceProvider, object?, IIdentity> DeniedIdentityFactory =
        static (sp, _) => new BoundIdentity(WrapPort(sp.GetService<IIdentityDirectoryPort>() ?? DeniedIdentityDirectoryPort.Instance), Callers(sp));

    internal static readonly Func<IServiceProvider, object?, IGrants> DeniedGrantsFactory =
        static (sp, _) => new BoundIdentity(WrapPort(sp.GetService<IIdentityDirectoryPort>() ?? DeniedIdentityDirectoryPort.Instance), Callers(sp));

    internal static readonly Func<IServiceProvider, object?, ISecrets> DeniedSecretsFactory =
        static (sp, _) => new SecretsCapability(
            sp.GetService<ISecretVaultPort>() ?? DeniedSecretVaultPort.Instance,
            sp.GetService<ILogger<SecretsCapability>>(),
            Callers(sp))
        {
            Declarations = TopologyHosting.ComposedDeclarations(sp),
        };

    internal static readonly Func<IServiceProvider, object?, BoundSessions> DeniedSessionsCapabilityFactory =
        static (sp, _) => new BoundSessions(
            WrapPort(sp.GetService<IIdentitySessionPort>() ?? DeniedIdentitySessionPort.Instance),
            Callers(sp),
            TopologyHosting.ComposedDeclarations(sp).RequirePrivileged);

    internal static readonly Func<IServiceProvider, object?, ISessions> DeniedSessionsFactory =
        static (sp, key) => sp.GetRequiredKeyedService<BoundSessions>(key);

    internal static readonly Func<IServiceProvider, object?, IIdentityProvider> DeniedIdentityDescriptorFactory =
        static (sp, key) => sp.GetRequiredKeyedService<BoundSessions>(key);

    /// <summary>
    /// Configures the one <see cref="CapabilityRegistry"/> of this host (for example <c>AcceptBinding(storage@1, typeof(IStorage))</c>).
    /// Call before the silo is configured; module validation runs there.
    /// </summary>
    public static IHostApplicationBuilder ConfigureCapabilities(this IHostApplicationBuilder builder, Action<CapabilityRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(Registry(builder.Services));
        return builder;
    }

    /// <summary>The registry instance in <paramref name="services"/>, added when absent.</summary>
    internal static CapabilityRegistry Registry(IServiceCollection services)
    {
        if (services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(CapabilityRegistry) && !descriptor.IsKeyedService)?.ImplementationInstance
            is CapabilityRegistry existing)
        {
            return existing;
        }
        var registry = new CapabilityRegistry();
        services.AddSingleton(registry);
        return registry;
    }

    internal static bool IsDeniedIdentityFallback(ServiceDescriptor descriptor)
        => descriptor.IsKeyedService
            && (ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedIdentityFactory)
                || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedGrantsFactory)
                || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedSecretsFactory)
                || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedSessionsCapabilityFactory)
                || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedSessionsFactory)
                || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedIdentityDescriptorFactory));

    internal static void RemoveDeniedIdentityFallbacks(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.IsKeyedService
                && (ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedIdentityFactory)
                    || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedGrantsFactory)))
            {
                services.RemoveAt(index);
            }
        }
    }

    internal static void RemoveDeniedSecretsFallbacks(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.IsKeyedService && ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedSecretsFactory))
            {
                services.RemoveAt(index);
            }
        }
    }

    internal static void RemoveDeniedSessionsFallbacks(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.IsKeyedService
                && (ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedSessionsCapabilityFactory)
                    || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedSessionsFactory)
                    || ReferenceEquals(descriptor.KeyedImplementationFactory, DeniedIdentityDescriptorFactory)))
            {
                services.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// Lightweight metadata recorded at <see cref="IServiceCollection"/> registration time for a host capability port,
    /// allowing <see cref="Hosting.NeuronTypeRegistry"/> and <see cref="SignalTypeMap"/> to discover port assemblies and
    /// scoping modules without instantiating live port singletons during DI graph startup (preventing circular dependencies).
    /// </summary>
    internal sealed record RegisteredPortDescriptor(Type ServiceType, Type PortType);

    internal static void CapturePortDescriptors(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.IsKeyedService)
            {
                continue;
            }

            if (descriptor.ServiceType != typeof(ISecretVaultPort)
                && descriptor.ServiceType != typeof(IIdentityDirectoryPort)
                && descriptor.ServiceType != typeof(ICapabilityConnections))
            {
                continue;
            }

            var portType = ResolvePortType(descriptor);
            if (portType is null)
            {
                continue;
            }

            var exists = services.Any(existing =>
                existing.ServiceType == typeof(RegisteredPortDescriptor)
                && existing.ImplementationInstance is RegisteredPortDescriptor r
                && r.ServiceType == descriptor.ServiceType
                && r.PortType == portType);
            if (!exists)
            {
                services.AddSingleton(new RegisteredPortDescriptor(descriptor.ServiceType, portType));
            }
        }
    }

    private static Type? ResolvePortType(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { IsInterface: false, IsAbstract: false } implType)
        {
            return implType;
        }

        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance.GetType();
        }

        if (descriptor.ImplementationFactory?.Method.ReturnType is { IsInterface: false, IsAbstract: false } retType
            && retType != typeof(object))
        {
            return retType;
        }

        return null;
    }

    internal static IEnumerable<Assembly> RegisteredPortAssemblies(IServiceProvider services)
    {
        var descriptors = services.GetServices<RegisteredPortDescriptor>().ToArray();
        var hasDirectoryDescriptor = false;
        var hasVaultDescriptor = false;
        var hasConnectionsDescriptor = false;

        foreach (var descriptor in descriptors)
        {
            if (descriptor.ServiceType == typeof(IIdentityDirectoryPort))
            {
                hasDirectoryDescriptor = true;
                yield return descriptor.PortType.Assembly;
                yield return typeof(IIdentity).Assembly;
            }
            else if (descriptor.ServiceType == typeof(ISecretVaultPort))
            {
                hasVaultDescriptor = true;
                yield return descriptor.PortType.Assembly;
            }
            else if (descriptor.ServiceType == typeof(ICapabilityConnections))
            {
                hasConnectionsDescriptor = true;
                yield return descriptor.PortType.Assembly;
                yield return typeof(ICapabilityConnections).Assembly;
            }
        }

        if (!hasDirectoryDescriptor
            && services.GetService<IIdentityDirectoryPort>() is { } directory
            && !ReferenceEquals(directory, DeniedIdentityDirectoryPort.Instance))
        {
            var unwrapped = directory is PlatformIdentityDirectoryPort platform ? platform.Inner : directory;
            yield return unwrapped.GetType().Assembly;
            yield return typeof(IIdentity).Assembly;
        }
        if (!hasVaultDescriptor
            && services.GetService<ISecretVaultPort>() is { } vault
            && !ReferenceEquals(vault, DeniedSecretVaultPort.Instance))
        {
            yield return vault.GetType().Assembly;
        }
        if (!hasConnectionsDescriptor
            && services.GetService<ICapabilityConnections>() is { } connections
            && !ReferenceEquals(connections, DeniedCapabilityConnections.Instance))
        {
            yield return connections.GetType().Assembly;
            yield return typeof(ICapabilityConnections).Assembly;
        }
    }

    /// <summary>
    /// Returns any <see cref="DigitalBrain.Modules.Module"/> types declared in the same namespace as a registered host port,
    /// so <see cref="Hosting.NeuronTypeRegistry"/> and <see cref="SignalTypeMap"/> scope multi-module port assemblies
    /// (such as <c>DigitalBrain.Identity.Local</c>) to only the backing neurons/signals of the ports actually registered on this host.
    /// Uses registration-time <see cref="RegisteredPortDescriptor"/> metadata first to avoid instantiating live port singletons.
    /// </summary>
    internal static IEnumerable<Type> RegisteredPortModules(IServiceProvider services)
    {
        var descriptors = services.GetServices<RegisteredPortDescriptor>().ToArray();
        var hasDirectoryDescriptor = false;
        var hasVaultDescriptor = false;

        foreach (var descriptor in descriptors)
        {
            if (descriptor.ServiceType == typeof(IIdentityDirectoryPort))
            {
                hasDirectoryDescriptor = true;
                foreach (var module in SameNamespaceModules(descriptor.PortType))
                {
                    yield return module;
                }
            }
            else if (descriptor.ServiceType == typeof(ISecretVaultPort))
            {
                hasVaultDescriptor = true;
                foreach (var module in SameNamespaceModules(descriptor.PortType))
                {
                    yield return module;
                }
            }
        }

        if (!hasDirectoryDescriptor
            && services.GetService<IIdentityDirectoryPort>() is { } directory
            && !ReferenceEquals(directory, DeniedIdentityDirectoryPort.Instance))
        {
            var unwrapped = directory is PlatformIdentityDirectoryPort platform ? platform.Inner : directory;
            foreach (var module in SameNamespaceModules(unwrapped.GetType()))
            {
                yield return module;
            }
        }

        if (!hasVaultDescriptor
            && services.GetService<ISecretVaultPort>() is { } vault
            && !ReferenceEquals(vault, DeniedSecretVaultPort.Instance))
        {
            foreach (var module in SameNamespaceModules(vault.GetType()))
            {
                yield return module;
            }
        }
    }

    private static IEnumerable<Type> SameNamespaceModules(Type portType)
    {
        var ns = portType.Namespace;
        if (string.IsNullOrEmpty(ns))
        {
            yield break;
        }

        Type[] types;
        try { types = portType.Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = [.. ex.Types.OfType<Type>()]; }

        foreach (var type in types)
        {
            if (type is { IsClass: true, IsAbstract: false }
                && string.Equals(type.Namespace, ns, StringComparison.Ordinal)
                && typeof(DigitalBrain.Modules.Module).IsAssignableFrom(type)
                && type.IsDefined(typeof(DigitalBrain.Modules.ModuleAttribute), inherit: false))
            {
                yield return type;
            }
        }
    }

    /// <summary>Registers the broker over the host's <see cref="ICapabilityConnections"/> and the single-registration startup check.</summary>
    internal static void AddCapabilityBroker(this IServiceCollection services)
    {
        CapturePortDescriptors(services);
        var registry = Registry(services);
        services.TryAddSingleton<DigitalBrain.Runtime.Orleans.Capabilities.Storage.StorageAccessLog>();
        if (!services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IIdentity) && Capability.Identity.V1Id.Equals(descriptor.ServiceKey as string, StringComparison.Ordinal)))
        {
            services.AddKeyedSingleton(Capability.Identity.V1Id, DeniedIdentityFactory);
        }
        if (!services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IGrants) && Capability.Identity.V1Id.Equals(descriptor.ServiceKey as string, StringComparison.Ordinal)))
        {
            services.AddKeyedSingleton(Capability.Identity.V1Id, DeniedGrantsFactory);
        }
        if (!services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ISecrets) && Capability.Secrets.V1Id.Equals(descriptor.ServiceKey as string, StringComparison.Ordinal)))
        {
            services.AddKeyedSingleton(Capability.Secrets.V1Id, DeniedSecretsFactory);
        }
        var hasSessionPort = services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IIdentitySessionPort))
            || services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ISessions) && Capability.Identity.V1Id.Equals(descriptor.ServiceKey as string, StringComparison.Ordinal));
        if (hasSessionPort)
        {
            if (!services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(BoundSessions) && Capability.Identity.V1Id.Equals(descriptor.ServiceKey as string, StringComparison.Ordinal)))
            {
                services.AddKeyedSingleton(Capability.Identity.V1Id, DeniedSessionsCapabilityFactory);
            }
            if (!services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ISessions) && Capability.Identity.V1Id.Equals(descriptor.ServiceKey as string, StringComparison.Ordinal)))
            {
                services.AddKeyedSingleton(Capability.Identity.V1Id, DeniedSessionsFactory);
            }
            if (!services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IIdentityProvider) && Capability.Identity.V1Id.Equals(descriptor.ServiceKey as string, StringComparison.Ordinal)))
            {
                services.AddKeyedSingleton(Capability.Identity.V1Id, DeniedIdentityDescriptorFactory);
            }
            services.TryAddSingleton<ISessions>(sp => sp.GetRequiredKeyedService<ISessions>(Capability.Identity.V1Id));
            services.TryAddSingleton<IIdentityProvider>(sp => sp.GetRequiredKeyedService<IIdentityProvider>(Capability.Identity.V1Id));
        }
        services.TryAddSingleton<IIdentity>(sp => sp.GetRequiredKeyedService<IIdentity>(Capability.Identity.V1Id));
        services.TryAddSingleton<IGrants>(sp => sp.GetRequiredKeyedService<IGrants>(Capability.Identity.V1Id));
        services.TryAddSingleton<ISecrets>(sp => sp.GetRequiredKeyedService<ISecrets>(Capability.Secrets.V1Id));
        services.TryAddSingleton(sp => new CapabilityBroker(
            registry,
            sp.GetService<ICapabilityConnections>() ?? DeniedCapabilityConnections.Instance,
            sp,
            sp.GetServices<ICapabilityBindingInterceptor>()));
        services.TryAddSingleton<ICapabilityBroker>(sp => sp.GetRequiredService<CapabilityBroker>());
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(new SingleCapabilityCheck(registry, services));
    }

    // Fails silo startup when a single-connection capability is registered twice; reads the collection at start, after host registrations.
    private sealed class SingleCapabilityCheck(CapabilityRegistry registry, IServiceCollection services) : ILifecycleParticipant<ISiloLifecycle>
    {
        public void Participate(ISiloLifecycle lifecycle)
            => lifecycle.Subscribe(nameof(SingleCapabilityCheck), ServiceLifecycleStage.First, _ =>
            {
                var issues = CapabilityBroker.SingleRegistrationIssues(registry, services);
                return issues.Count == 0 ? Task.CompletedTask : throw new InvalidOperationException(string.Join(Environment.NewLine, issues));
            });
    }

    private sealed class DeniedCapabilityConnections : ICapabilityConnections
    {
        public static readonly DeniedCapabilityConnections Instance = new();

        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string key, string callerApp, CancellationToken ct) where TCapability : class
            => throw new UnauthorizedAccessException("connections@1: no connections service is registered on this host.");

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class DeniedIdentityDirectoryPort : IIdentityDirectoryPort
    {
        public static readonly DeniedIdentityDirectoryPort Instance = new();

        private static UnauthorizedAccessException Denied()
            => new("identity@1: no identity directory port is registered on this host.");

        public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken) => throw Denied();
        public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken) => throw Denied();
        public Task EnsureAccount(Account account, CancellationToken cancellationToken) => throw Denied();
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken) => throw Denied();
        public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken) => throw Denied();
        public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken) => throw Denied();
        public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken) => throw Denied();
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken) => throw Denied();
        public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken) => throw Denied();
        public Task<IReadOnlyList<DigitalBrain.Capabilities.Identity.BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken) => throw Denied();
        public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken) => throw Denied();
        public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken) => throw Denied();
        public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken) => throw Denied();
        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken) => throw Denied();
        public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken) => throw Denied();
        public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken) => throw Denied();
        public Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken)
            => Task.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.NotAuthenticated, "identity@1: no identity directory port is registered on this host."));
        public Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken) => throw Denied();
        public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken) => throw Denied();
        public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken) => throw Denied();
    }

    private sealed class DeniedSecretVaultPort : ISecretVaultPort
    {
        public static readonly DeniedSecretVaultPort Instance = new();

        public Task<SecretVaultResult> Set(string name, string label, string value, CancellationToken cancellationToken)
            => Task.FromResult(SecretVaultResult.Denied);

        public Task<SecretVaultStatus> Remove(string name, CancellationToken cancellationToken)
            => Task.FromResult(SecretVaultStatus.Denied);

        public Task<SecretVaultResult> Resolve(SecretRef secret, CancellationToken cancellationToken)
            => Task.FromResult(SecretVaultResult.Denied);
    }

    private sealed class DeniedIdentitySessionPort : IIdentitySessionPort
    {
        public static readonly DeniedIdentitySessionPort Instance = new();

        private static UnauthorizedAccessException Denied()
            => new("identity@1: no identity session port is registered on this host.");

        public Task<SessionIssued> Issue(SessionRequest request, CancellationToken cancellationToken) => throw Denied();
        public Task End(string sessionId, CancellationToken cancellationToken) => throw Denied();
        public Task<Member?> Validate(string sessionId, CancellationToken cancellationToken) => throw Denied();
        public IdentityProviderDescriptor Describe() => throw Denied();
    }
}
