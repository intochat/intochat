using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using IntoChat.OS.Modules.Microsoft.Orleans;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace IntoChat.OS.Testing;

// The product recipe in the test process: AddIntoChatOSServer + MapIntoChatOSServer on a loopback
// port, on Topology=local (localhost clustering, file grain stores and reminders) over a temporary Home
// that a restart builds over again, so grain state outlives the restart.
public sealed class InProcessTarget : ITestTarget
{
    private const int StartAttempts = 3;
    private readonly Func<PortReservation> _ports;

    public InProcessTarget() : this(() => PortReservation.Reserve(2)) { }

    internal InProcessTarget(Func<PortReservation> ports) => _ports = ports;

    public async Task<ITestBrain> StartAsync(TestBrainPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Client is not null) { throw new NotSupportedException("An in-process brain has no external client to configure; use ConfigureServices."); }
        for (var attempt = 1; ; attempt++)
        {
            // Orleans refuses port 0, so parallel brains each reserve a pair (held until the silo started) and a restart
            // rebinds the same pair. A process outside the reservation can still take a port first: that starts over on a new pair.
            using var reservation = _ports();
            var (siloPort, gatewayPort) = (reservation.Ports[0], reservation.Ports[1]);
            var session = new InProcessSession(
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Path.Combine(Path.GetTempPath(), $"intochat-os-machine-{Guid.NewGuid():N}"),
                TestHome.Create(), siloPort, gatewayPort);
            try
            {
                reservation.ReleaseSockets();
                var app = await BuildAsync(plan, session, httpPort: 0, cancellationToken).ConfigureAwait(false);
                return new InProcessBrain(plan, session, app, new TestSessionLifetime(plan.Execution));
            }
            catch (Exception error)
            {
                if (Directory.Exists(session.MachineDirectory)) { Directory.Delete(session.MachineDirectory, recursive: true); }
                session.Home.Dispose();
                if (attempt == StartAttempts || !AddressInUse(error)) { throw; }
                plan.Execution.Diagnostics?.Invoke(new TestDiagnostic("in-process-start",
                    $"Ports {siloPort}/{gatewayPort} were taken before the silo bound them; retrying ({attempt}/{StartAttempts - 1})."));
            }
        }
    }

    private static bool AddressInUse(Exception? error) => error switch
    {
        null => false,
        global::Microsoft.AspNetCore.Connections.AddressInUseException => true,
        // Windows reports a port another socket holds exclusively (or a reserved range) as access denied.
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(AddressInUse),
        _ => AddressInUse(error.InnerException),
    };

    internal static async Task<WebApplication> BuildAsync(TestBrainPlan plan, InProcessSession session, int httpPort, CancellationToken cancellationToken)
    {
        // Neither Development (which changes feature and token behaviour) nor Production (which refuses configured custody).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        if (plan.Execution.Http) { builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}"); }
        // Without UseUrls Kestrel would still listen on its default port; slim hosts replace it with a server that never binds.
        else { builder.Services.AddSingleton<global::Microsoft.AspNetCore.Hosting.Server.IServer, NoHttpServer>(); }
        builder.Configuration.AddInMemoryCollection(TestLogging.QuietDefaults);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntoChatOSNames.MasterKeyConfigurationKey] = session.MasterKey,
            // A temporary Home per brain: the brain log lives in <Home>/state, never in the developer's ~/.intochat.
            [IntoChatOSNames.HomeKey] = session.Home.Path,
            [IntegrationSchema.Key("csharp", "RunTokenKey")] = session.RunTokenKey,
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [global::DigitalBrain.Runtime.Orleans.TopologyContext.SiloPortKey] = session.SiloPort.ToString(CultureInfo.InvariantCulture),
            [global::DigitalBrain.Runtime.Orleans.TopologyContext.GatewayPortKey] = session.GatewayPort.ToString(CultureInfo.InvariantCulture),
            // The AppHost's Orleans resource turns grain-call tracing on for every silo; the test host does when the fact collects diagnostics.
            ["Orleans:EnableDistributedTracing"] = plan.Execution.Diagnostics is null ? "false" : "true",
        });
        foreach (var module in plan.Modules) { builder.Configuration.AddInMemoryCollection(module.Settings); }
        builder.Configuration.AddInMemoryCollection(plan.Configuration);
        builder.Services.AddSingleton<ActivationIndex>();
        builder.Services.AddSingleton<IActivationWorkingSetObserver>(services => services.GetRequiredService<ActivationIndex>());
        builder.AddIntoChatOSServer(server =>
        {
            if (plan.LicenseCheck is { } check) { server.UseLicenseCheck(check); }
            server.AddModule<OrleansModule>();
            foreach (var module in plan.Modules.Where(module => module.Module is not OrleansModule)) { server.AddModule(module.Module); }
            foreach (var module in plan.NeuronModules ?? []) { server.AddNeuronModule(module); }
        });
        if (plan.Services is { } services)
        {
            // After the server's and Orleans' registrations, so a fact replaces or decorates theirs; the kernel wraps the
            // cluster storage after it, so a storage the fact replaced or decorated is wrapped once, outermost.
            builder.UseOrleans(silo =>
            {
                global::IntoChat.OS.Kernel.Composition.BrainHosting.TryRemoveKernelRegistration(silo);
                services(silo.Services);
                global::IntoChat.OS.Kernel.Composition.BrainHosting.TryAddKernelRegistration(silo);
            });
        }
        var app = builder.Build();
        try
        {
            if (plan.Execution.Http) { app.MapIntoChatOSServer(); }
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            await LocalSiloInManifestAsync(app.Services, cancellationToken).ConfigureAwait(false);
            return app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The host is ready once the cluster manifest lists this silo's grains. Before that, placement finds no
    // compatible silo and retries after a delay on the host's TimeProvider, which never elapses under a fake one.
    private static async Task LocalSiloInManifestAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var local = services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        var manifests = services.GetRequiredService<IClusterManifestProvider>();
        if (manifests.Current.Silos.ContainsKey(local)) { return; }
        await foreach (var manifest in manifests.Updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (manifest.Silos.ContainsKey(local)) { return; }
        }
    }
}

internal sealed record InProcessSession(string MasterKey, string RunTokenKey, string MachineDirectory, TestHome Home, int SiloPort, int GatewayPort);

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

internal sealed class InProcessBrain : ITestBrain, ITrackedBrain, ISignalDelivery, IntoChat.OS.Runtime.IPlainClient
{
    private readonly TestBrainPlan _plan;
    private readonly InProcessSession _session;
    private readonly TestSessionLifetime _lifetime;
    private WebApplication? _app;
    private HttpClient? _http;
    // Observations hold subscriptions on the current host; a restart ends them with it.
    private TestSessionLifetime _observations;
    private bool _disposed;

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
            _session.Home.Dispose();
            return ValueTask.CompletedTask;
        }));
        _lifetime.Own("host", new Release(StopHostAsync));
        _lifetime.Own("http-client", new Release(() => { _http?.Dispose(); return ValueTask.CompletedTask; }));
        _lifetime.Own("observations", new Release(() => _observations.DisposeAsync()));
    }

    public HttpClient HttpClient => _http ?? throw new InvalidOperationException("This host was started WithoutHttp()");
    public IServiceProvider Services
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_app ?? throw new InvalidOperationException("The brain's host failed to restart.")).Services;
        }
    }
    public TestExecutionOptions Execution => _plan.Execution;
    int ITrackedBrain.BufferCapacity => Services.GetRequiredService<IOptions<SubscriptionOptions>>().Value.BufferCapacity;
    void ITrackedBrain.Track(IAsyncDisposable resource) => _observations.Own("observation", resource);

    private IIntoChatOSBrains Brains => Services.GetRequiredService<IIntoChatOSBrains>();

    public T Get<T>(string id) where T : class, INeuron => Brains.Grain<T>(id);
    T IIntoChatOS.Get<T>(NeuronId id) => Brains.Grain<T>(id.Name);
    public T Neuron<T>(NeuronId relative) where T : INeuron => Brains.Grain<T>(relative.Within(BrainScope.CurrentId()).Name);
    public IBrain Self => Brains.Open(BrainScope.CurrentId()).Self;

    // 06-07b: plain neurons through the product's in-process client, so facts read the same for grains and plain neurons.
    object IntoChat.OS.Runtime.IPlainClient.GetPlain(Type contract, NeuronId id) => ((IntoChat.OS.Runtime.IPlainClient)Brains).GetPlain(contract, id);

    IAsyncEnumerable<T> IntoChat.OS.Runtime.IPlainClient.OnPlain<T>(object source, Func<Task> onSubscribed, CancellationToken cancellationToken)
        => ((IntoChat.OS.Runtime.IPlainClient)Brains).OnPlain<T>(source, onSubscribed, cancellationToken);
    public IKernelView Kernel => Brains.Open(BrainScope.CurrentId()).Kernel;

    public Task<ISignalSubscription<T>> SubscribeAsync<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        => Services.GetRequiredService<IBrainSubscriptions>().SubscribeAsync<T>(source, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => Brains.On<T>(neuron, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => Brains.On<T>(neuron, onSubscribed, cancellationToken);

    Task ISignalDelivery.DeliveredAsync(CancellationToken cancellationToken)
        => Services.GetService<ILocalSignalHub>() is { } hub ? hub.FlushAsync(cancellationToken) : Task.CompletedTask;

    public async Task DeactivateAsync(IAddressable neuron, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        if (Services.GetRequiredService<ActivationIndex>().Find(neuron.GetGrainId()) is not { } context) { return; }
        context.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "The test deactivated it."), cancellationToken);
        await context.Deactivated.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var app = _app ?? throw new InvalidOperationException("The brain's host failed to restart.");
        var port = _plan.Execution.Http ? new Uri(app.Urls.Single()).Port : 0;
        var observations = _observations;
        _observations = new TestSessionLifetime(_plan.Execution);
        await observations.DisposeAsync().ConfigureAwait(false);
        await StopHostAsync().ConfigureAwait(false);
        _app = await InProcessTarget.BuildAsync(_plan, _session, port, cancellationToken).ConfigureAwait(false);
        var previous = _http;
        _http = NewClient(_app);
        previous?.Dispose();
    }

    public async Task<Person> PersonAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var person = _http is { } http
            ? await Person.RegisterAsync(http, name, cancellationToken).ConfigureAwait(false)
            // No HTTP edge: the identity directory, which runs the registration the /identity/register endpoint runs.
            : Person.Registered(await Services.GetRequiredService<IGrainFactory>().GetGrain<IIdentityDirectory>(Services.GetRequiredService<IOptions<IdentityHostOptions>>().Value.DirectoryKey)
                .RegisterAsync(name, Person.Password(name), name, cancellationToken).ConfigureAwait(false));
        _lifetime.Own("person", new Release(() => { person.Dispose(); return ValueTask.CompletedTask; }));
        return person;
    }

    public Person Owner => Services.GetRequiredService<IOptions<AuthOptions>>().Value.Posture == IdentityPosture.Open
        ? Person.OpenOwner(_http)
        : throw new InvalidOperationException("Only the Open posture has a synthetic owner; register a person with PersonAsync.");

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _lifetime.DisposeAsync();
    }

    private async ValueTask StopHostAsync()
    {
        if (_app is not { } app) { return; }
        _app = null;
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private HttpClient? NewClient(WebApplication app) => _plan.Execution.Http ? new() { BaseAddress = new Uri(app.Urls.Single()) } : null;

    private sealed class Release(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }
}
