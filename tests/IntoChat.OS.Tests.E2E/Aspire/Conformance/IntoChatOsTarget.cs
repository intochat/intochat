using System.Reflection;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Hosting;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Testing.Conformance.Host;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Serialization.Cloning;
using IntoChat.OS.Modules.Identity;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Conformance;

public sealed class IntoChatOsTarget : OrleansFrameworkTarget
{
    public const string TargetName = "intochat-os";

    public override string Name => TargetName;

    public static IReadOnlyList<Type> ExpectedOsModules()
        =>
        [
            .. OperatingSystemModules.InProcessModules.Select(static module => module.Neurons).OfType<Type>()
                .Append(typeof(AuthorityPlainNeurons))
                .Append(typeof(DigitalBrain.Graph.GraphModule))
                .Distinct(),
        ];

    public static IReadOnlyList<Type> Modules { get; } =
    [
        .. ExpectedOsModules().Append(typeof(FrameworkModule)).Distinct(),
    ];

    private static Assembly[] SignalAssemblies()
        =>
        [
            .. Modules.Select(static module => module.Assembly)
                .Concat(OperatingSystemModules.InProcessModules
                    .Where(static module => module.Neurons is not null)
                    .Select(static module => module.Contracts)
                    .OfType<Assembly>())
                .Append(typeof(Signal).Assembly)
                .Append(typeof(IBrain).Assembly)
                .Append(typeof(IConnection).Assembly)
                .Append(typeof(IConnectionRequests).Assembly)
                .Distinct(),
        ];

    public override Task<IInProcessHost> Start(CancellationToken ct)
    {
        var clock = new OffsetClock();
        return StartInProcessAsync(new InProcessHostOptions { Modules = Modules }, clock, ct);
    }

    public override async Task<RunningTarget> StartHost(FrameworkHostOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clock = new OffsetClock();
        var host = await StartInProcessAsync(
            new InProcessHostOptions
            {
                Modules = Modules,
                Configuration = OrleansRun.Configuration(options),
            },
            clock,
            ct);
        return OrleansRun.Started(host, options, clock);
    }

    private static async Task<IInProcessHost> StartInProcessAsync(InProcessHostOptions options, OffsetClock clock, CancellationToken ct)
    {
        var temporary = options.Home is null;
        var home = Path.GetFullPath(options.Home ?? Path.Combine(Path.GetTempPath(), "digitalbrain-" + Guid.NewGuid().ToString("N")));
        var host = new KernelInProcessHost(options, home, temporary, clock);
        try
        {
            await host.StartAsync(ct);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private sealed class KernelInProcessHost(InProcessHostOptions options, string home, bool temporary, OffsetClock clock) : IInProcessHost
    {
        private static readonly BrainStorageRegistration ClusterStore = new(SignalAssemblies());
        private Microsoft.Extensions.Hosting.IHost? _host;
        private PortLease? _portLease;

        public IServiceProvider Services => (_host ?? throw new ObjectDisposedException(nameof(KernelInProcessHost))).Services;

        public string Home => home;

        public async Task StartAsync(CancellationToken ct)
        {
            var lease = PortReservation.ReservePair();
            _portLease = lease;
            try
            {
                var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings());
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [RuntimeOptions.Default.TopologyKey] = Topology.Local.ToConfigValue(),
                    [RuntimeOptions.Default.HomeKey] = home,
                    [TopologyContext.SiloPortKey] = lease.SiloPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [TopologyContext.GatewayPortKey] = lease.GatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                foreach (var (key, value) in options.Configuration) { values[key] = value; }
                Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(builder.Configuration, values);
                builder.UseRuntimeOptions(RuntimeOptions.Default);
                builder.UseClusterStore(ClusterStore);
                builder.ConfigureCapabilities(IntoChat.OS.Kernel.Composition.Capabilities.KernelCapabilities.Configure);
                builder.UseModuleTopology(options.Modules, []);
                Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans(builder, silo =>
                {
                    silo.ConfigureTopologySilo();
                    OrleansRun.ClockServices(clock)(silo.Services);
                    silo.Services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
                });
                _host = builder.Build();
                await _host.StartAsync(ct);
            }
            catch
            {
                lease.Dispose();
                _portLease = null;
                throw;
            }
        }

        public async Task Restart(CancellationToken ct)
        {
            await StopAsync(ct);
            await StartAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync(CancellationToken.None);
            }
            finally
            {
                if (temporary)
                {
                    DigitalBrain.Testing.TempDirectory.DeleteIfPresent(home);
                }
            }
        }

        private async Task StopAsync(CancellationToken ct)
        {
            try
            {
                if (_host is null) { return; }
                var host = _host;
                _host = null;
                try { await host.StopAsync(ct); }
                finally
                {
                    if (host is IAsyncDisposable asyncDisposable) { await asyncDisposable.DisposeAsync(); }
                    else { host.Dispose(); }
                }
            }
            finally
            {
                _portLease?.Dispose();
                _portLease = null;
            }
        }
    }

    private sealed class EmptyCapabilityConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string key, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new InvalidOperationException($"No connection bound for '{key}'.");

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
