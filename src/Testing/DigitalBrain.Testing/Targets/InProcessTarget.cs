using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Orleans;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Parts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace DigitalBrain.Testing;

// The product recipe in the test process: AddDigitalBrainServer + MapDigitalBrainServer on a loopback
// port, the orleans integration absent (localhost clustering, memory reminders) and grain state in a
// harness-owned MemoryStores that a restart builds over again.
public sealed class InProcessTarget : ITestTarget
{
    public async Task<ITestBrain> StartAsync(TestBrainPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Client is not null) { throw new NotSupportedException("An in-process brain has no external client to configure; use ConfigureServices."); }
        var (siloPort, gatewayPort) = FreePorts();
        var session = new InProcessSession(
            new MemoryStores(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Path.Combine(Path.GetTempPath(), $"digitalbrain-machine-{Guid.NewGuid():N}"),
            // Localhost clustering binds fixed default ports; parallel brains each take a free pair, and a
            // restart rebinds the same pair.
            siloPort, gatewayPort);
        var app = await BuildAsync(plan, session, httpPort: 0, cancellationToken).ConfigureAwait(false);
        return new InProcessBrain(plan, session, app, new TestSessionLifetime(plan.Execution));
    }

    internal static async Task<WebApplication> BuildAsync(TestBrainPlan plan, InProcessSession session, int httpPort, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");
        builder.Configuration.AddInMemoryCollection(TestLogging.QuietDefaults);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DigitalBrainNames.MasterKeyConfigurationKey] = session.MasterKey,
            [IntegrationSchema.Key("csharp", "RunTokenKey")] = session.RunTokenKey,
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [$"{PartsOptions.Key}:MachineDirectory"] = session.MachineDirectory,
            [OrleansStorageHosting.SiloPortKey] = session.SiloPort.ToString(CultureInfo.InvariantCulture),
            [OrleansStorageHosting.GatewayPortKey] = session.GatewayPort.ToString(CultureInfo.InvariantCulture),
        });
        foreach (var module in plan.Modules) { builder.Configuration.AddInMemoryCollection(module.Settings); }
        builder.Configuration.AddInMemoryCollection(plan.Configuration);
        // Before the orleans module configures: its TryAddSingleton keeps this instance.
        builder.Services.AddSingleton(session.Stores);
        builder.Services.AddSingleton<ActivationIndex>();
        builder.Services.AddSingleton<IActivationWorkingSetObserver>(services => services.GetRequiredService<ActivationIndex>());
        builder.AddDigitalBrainServer(server =>
        {
            server.AddModule<OrleansModule>();
            foreach (var module in plan.Modules.Where(module => module.Module is not OrleansModule)) { server.AddModule(module.Module); }
            if (plan.Services is { } services) { server.ConfigureServices(services); }
        });
        var app = builder.Build();
        try
        {
            app.MapDigitalBrainServer();
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Both listeners stay open while the ports are read, so the pair is distinct.
    private static (int Silo, int Gateway) FreePorts()
    {
        var silo = new TcpListener(IPAddress.Loopback, 0);
        var gateway = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            silo.Start();
            gateway.Start();
            return (((IPEndPoint)silo.LocalEndpoint).Port, ((IPEndPoint)gateway.LocalEndpoint).Port);
        }
        finally
        {
            silo.Stop();
            gateway.Stop();
        }
    }
}

internal sealed record InProcessSession(MemoryStores Stores, string MasterKey, string RunTokenKey, string MachineDirectory, int SiloPort, int GatewayPort);

// The silo's live activations by grain id, so a fact can deactivate one and wait for it to finish.
internal sealed class ActivationIndex : IActivationWorkingSetObserver
{
    private readonly ConcurrentDictionary<GrainId, IGrainContext> _active = new();

    public IGrainContext? Find(GrainId id) => _active.TryGetValue(id, out var context) ? context : null;

    public void OnAdded(IActivationWorkingSetMember member)
    {
        if (member is IGrainContext context) { _active[context.GrainId] = context; }
    }

    public void OnActive(IActivationWorkingSetMember member) { }
    public void OnIdle(IActivationWorkingSetMember member) { }
    public void OnEvicted(IActivationWorkingSetMember member) => Forget(member);
    public void OnDeactivating(IActivationWorkingSetMember member) { }
    public void OnDeactivated(IActivationWorkingSetMember member) => Forget(member);

    private void Forget(IActivationWorkingSetMember member)
    {
        if (member is IGrainContext context) { _active.TryRemove(new KeyValuePair<GrainId, IGrainContext>(context.GrainId, context)); }
    }
}

internal sealed class InProcessBrain : ITestBrain, ITrackedBrain
{
    private readonly TestBrainPlan _plan;
    private readonly InProcessSession _session;
    private readonly TestSessionLifetime _lifetime;
    private WebApplication? _app;
    private HttpClient _http;
    // Observations hold subscriptions on the current host; a restart ends them with it.
    private TestSessionLifetime _observations;

    public InProcessBrain(TestBrainPlan plan, InProcessSession session, WebApplication app, TestSessionLifetime lifetime)
    {
        _plan = plan;
        _session = session;
        _app = app;
        _lifetime = lifetime;
        _http = NewClient(app);
        _observations = new TestSessionLifetime(plan.Execution);
        // Released in reverse: observations, the client, the host current at disposal, the machine directory.
        _lifetime.Own("machine-directory", new Release(() =>
        {
            if (Directory.Exists(_session.MachineDirectory)) { Directory.Delete(_session.MachineDirectory, recursive: true); }
            return ValueTask.CompletedTask;
        }));
        _lifetime.Own("host", new Release(StopHostAsync));
        _lifetime.Own("http-client", new Release(() => { _http.Dispose(); return ValueTask.CompletedTask; }));
        _lifetime.Own("observations", new Release(() => _observations.DisposeAsync()));
    }

    public HttpClient HttpClient => _http;
    public IServiceProvider Services => (_app ?? throw new InvalidOperationException("The brain's host failed to restart.")).Services;
    public TestExecutionOptions Execution => _plan.Execution;
    int ITrackedBrain.BufferCapacity => Services.GetRequiredService<IOptions<SubscriptionOptions>>().Value.BufferCapacity;
    void ITrackedBrain.Track(IAsyncDisposable resource) => _observations.Own("observation", resource);

    private IDigitalBrains Brains => Services.GetRequiredService<IDigitalBrains>();

    public T Get<T>(string id) where T : class, INeuron => Brains.Grain<T>(id);
    T IDigitalBrain.Get<T>(NeuronId id) => Brains.Grain<T>(id.Name);
    public IBrain Self => Brains.Open(BrainScope.CurrentId()).Self;
    public IKernelView Kernel => Brains.Open(BrainScope.CurrentId()).Kernel;

    public Task<ISignalSubscription<T>> SubscribeAsync<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        => Services.GetRequiredService<IBrainSubscriptions>().SubscribeAsync<T>(source, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => Brains.On<T>(neuron, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => Brains.On<T>(neuron, onSubscribed, cancellationToken);

    public async Task DeactivateAsync(IAddressable neuron, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        if (Services.GetRequiredService<ActivationIndex>().Find(neuron.GetGrainId()) is not { } context) { return; }
        context.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "The test deactivated it."), cancellationToken);
        await context.Deactivated.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var port = new Uri((_app ?? throw new InvalidOperationException("The brain's host failed to restart.")).Urls.Single()).Port;
        var observations = _observations;
        _observations = new TestSessionLifetime(_plan.Execution);
        await observations.DisposeAsync().ConfigureAwait(false);
        await StopHostAsync().ConfigureAwait(false);
        _app = await InProcessTarget.BuildAsync(_plan, _session, port, cancellationToken).ConfigureAwait(false);
        var previous = _http;
        _http = NewClient(_app);
        previous.Dispose();
    }

    public Task<Person> PersonAsync(string name, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("People arrive with the identity helpers (one-host testing, Task 8).");

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();

    private async ValueTask StopHostAsync()
    {
        if (_app is not { } app) { return; }
        _app = null;
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private static HttpClient NewClient(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };

    private sealed class Release(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }
}
