using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Microsoft.Orleans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using System.Net.Http.Json;
using IntoChat.OS.ServiceDefaults;
using IntoChat.OS.Testing.Aspire.Diagnostics;

namespace IntoChat.OS.Testing.Aspire;

public sealed class AspireTestSession : IAsyncDisposable
{
    private readonly TestSessionLifetime _lifetime;
    private readonly TestExecutionOptions _options;
    private IHost? _client;
    private readonly IntoChatOSBuilder _brain;
    private readonly string _serverName;
    private AspireTestSession(DistributedApplication app, TestSessionLifetime lifetime, TestExecutionOptions options, IntoChatOSBuilder brain, string serverName)
    { App = app; _lifetime = lifetime; _options = options; _brain = brain; _serverName = serverName; }

    public DistributedApplication App { get; }
    public IIntoChatOSBrains Brains => _client!.Services.GetRequiredService<IIntoChatOSBrains>();
    public IBrainSubscriptions Subscriptions => _client!.Services.GetRequiredService<IBrainSubscriptions>();
    public HttpClient HttpClient { get; private set; } = null!;
    public Uri? BrowserEndpoint { get; private set; }
    public string? BrowserReadySelector { get; private set; }
    // Set when Chromium runs in the host's browser container rather than on this machine.
    public Uri? BrowserServer { get; private set; }
    public TestExecutionOptions Options => _options;
    public TestSessionLifetime Lifetime => _lifetime;
    public TestTelemetry? Telemetry { get; private set; }

    public ValueTask<string?> GetConnectionStringAsync(string resourceName, CancellationToken cancellationToken = default)
        => App.GetConnectionStringAsync(_brain.ResourceName(resourceName), cancellationToken);

    public static Task<AspireTestSession> StartAsync<TAppHost>(
        IReadOnlyList<string> args, TestExecutionOptions options, CancellationToken cancellationToken,
        BrowserLocation browser = BrowserLocation.Local)
        where TAppHost : class
        => StartCoreAsync(async ct =>
        {
            var telemetryEnabled = options.EnableDashboardTelemetry;
            var dashboardConfiguration = telemetryEnabled ? CreateDashboardTestingConfiguration() : null;
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<TAppHost>([.. args], (applicationOptions, settings) =>
            {
                settings.Configuration ??= new ConfigurationManager();
                settings.Configuration.AddInMemoryCollection(options.PrivateConfiguration);
                if (dashboardConfiguration is not null)
                {
                    applicationOptions.DisableDashboard = false;
                    applicationOptions.AllowUnsecuredTransport = true;
                    settings.Configuration.AddInMemoryCollection(dashboardConfiguration);
                    // These must win before the AppHost builder selects authentication services.
                    settings.Args = [.. settings.Args ?? [], .. dashboardConfiguration.Select(pair => $"{pair.Key}={pair.Value}")];
                    applicationOptions.Args = settings.Args;
                }
            }, ct).ConfigureAwait(false);
            if (dashboardConfiguration is not null)
            {
                builder.Configuration.AddInMemoryCollection(dashboardConfiguration);
                builder.Services.Configure<ResourceNotificationServiceOptions>(notificationOptions =>
                    notificationOptions.DefaultWaitBehavior = WaitBehavior.StopOnResourceUnavailable);
            }
            return builder;
        }, options, cancellationToken, browser);

    private static Dictionary<string, string?> CreateDashboardTestingConfiguration()
    {
        // Mirrors Aspire 13.6.1's EnableDashboard testing defaults while retaining the
        // configureBuilder callback needed for private configuration during AppHost composition.
        static string Token() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var browserToken = Token();
        var resourceKey = Token();
        return new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = string.Empty,
            ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = string.Empty,
            ["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = string.Empty,
            ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "http://127.0.0.1:0",
            ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true",
            ["ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "false",
            ["ASPIRE_INTERACTIVITY_ENABLED"] = "false",
            ["ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN"] = browserToken,
            ["ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY"] = resourceKey,
            ["AppHost:BrowserToken"] = browserToken,
            ["AppHost:ResourceService:AuthMode"] = "ApiKey",
            ["AppHost:ResourceService:ApiKey"] = resourceKey,
            ["AppHost:DashboardApiKey"] = Token(),
            ["AppHost:OtlpApiKey"] = Token(),
            ["AppHost:SuppressDashboardLoginUrlInStartupSummary"] = "true",
            ["ASPIRE_DASHBOARD_SUPPRESS_BROWSER_TOKEN_IN_OUTPUT"] = "true",
            ["ASPIRE_DASHBOARD_PERSISTENCE_MODE"] = "None",
            ["ASPIRE_DASHBOARD_API_DISABLED"] = "false"
        };
    }

    internal static void AllocateProjectHttpPorts(IEnumerable<IResource> resources)
    {
        foreach (var project in resources.OfType<ProjectResource>())
        {
            foreach (var endpoint in project.Annotations.OfType<EndpointAnnotation>().Where(e => e.UriScheme is "http" or "https"))
            {
                endpoint.Port = null;
                endpoint.TargetPort = null;
                endpoint.IsProxied = false;
            }
        }
    }

    internal static async Task<AspireTestSession> StartCoreAsync(
        Func<CancellationToken, Task<IDistributedApplicationTestingBuilder>> createBuilder,
        TestExecutionOptions options, CancellationToken cancellationToken, BrowserLocation browserLocation = BrowserLocation.Local)
    {
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.StartupTimeout);
        var ct = deadline.Token;
        var lifetime = new TestSessionLifetime(options);
        var stage = "builder";
        // A slow boot must name its stage: the timings print into the test output, so a
        // 15-minute fixture says whether the containers, the shell compile or the bundle
        // re-target ate the time.
        var bootWatch = System.Diagnostics.Stopwatch.StartNew();
        var stageWatch = System.Diagnostics.Stopwatch.StartNew();
        void Advance(string next)
        {
            Console.WriteLine($"[aspire-boot] {stage}: {stageWatch.Elapsed.TotalSeconds:F1}s");
            stage = next;
            stageWatch.Restart();
        }
        try
        {
            var builder = await createBuilder(ct).ConfigureAwait(false);
            lifetime.Own("builder", builder);
            PrivateTestConfiguration? privateSettings = null;
            if (options.PrivateConfiguration.Count > 0)
            {
                privateSettings = await PrivateTestConfiguration.CreateAsync(options.PrivateConfiguration, ct).ConfigureAwait(false);
                lifetime.Own("private-configuration", privateSettings);
            }
            // Provider parameters are evaluated when Aspire starts resources. Keep their
            // values out of command-line arguments and public composition envelopes.
            builder.Configuration.AddInMemoryCollection(options.PrivateConfiguration
                .Where(pair => pair.Key.StartsWith("Parameters:", StringComparison.OrdinalIgnoreCase)));
            // Aspire forwards every resource's console output under "<AppHost>.Resources.<resource>"; config rules beat SetMinimumLevel.
            builder.Configuration.AddInMemoryCollection(TestLogging.QuietDefaults);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Logging:LogLevel:{builder.Environment.ApplicationName}.Resources"] = "Warning",
            });
            builder.Configuration.AddInMemoryCollection(options.HostConfiguration);
            builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            var hosts = SiloHosts.Find(builder.Resources);
            var telemetryEnabled = options.EnableDashboardTelemetry;
            var telemetryResources = telemetryEnabled
                ? hosts.Cast<IResource>().Concat(builder.Resources.Where(resource => resource.Annotations.OfType<TelemetryExportBarrierAnnotation>().Any())).Distinct().ToArray()
                : [];
            var telemetryCapability = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            foreach (var resource in telemetryResources)
            {
                resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
                {
                    context.EnvironmentVariables[TelemetryExportBarrier.CapabilityEnvironmentVariable] = telemetryCapability;
                    context.EnvironmentVariables[IntoChatOSConfiguration.EnvironmentName(ServiceTelemetryOptions.SectionName, nameof(ServiceTelemetryOptions.SampleRatio))] = "1";
                    context.EnvironmentVariables["OTEL_BSP_MAX_QUEUE_SIZE"] = "65536";
                    context.EnvironmentVariables["OTEL_BLRP_MAX_QUEUE_SIZE"] = "65536";
                }));
            }
            var selectedHost = SiloHosts.Select(builder.Resources, options.ServerResourceName);
            var selectedBrain = SiloHosts.BrainOf(selectedHost);
            if (hosts.Count == 0)
            { throw new InvalidOperationException("The AppHost must reference the brain from at least one resource with an http endpoint."); }
            foreach (var resource in builder.Resources.Where(r => r is ProjectResource or ExecutableResource))
            {
                resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
                {
                    foreach (var (key, value) in TestLogging.QuietDefaults)
                    { context.EnvironmentVariables[IntoChatOSConfiguration.EnvironmentName(key)] = value!; }
                }));
            }
            AllocateProjectHttpPorts(builder.Resources);
            foreach (var resource in hosts)
            {
                if (privateSettings is not null)
                {
                    resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
                        context.EnvironmentVariables[ServerHostingExtensions.ConfigurationFileEnvironmentVariable] = privateSettings.FilePath));
                }
                // The harness exposes the host on the network on purpose, so it opts in to the Open-posture network check here, where the exposure is decided.
                // The silo binds every interface below, so the local bind policy is widened explicitly too (IntoChatOS:Bind, D-21).
                resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
                {
                    context.EnvironmentVariables[IntoChatOSConfiguration.EnvironmentName(
                        AuthOptions.Key(nameof(AuthOptions.AllowOpenOnNetwork)))] = "true";
                    context.EnvironmentVariables[IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.BindKey)] = "0.0.0.0";
                }));
                // The AppHost publishes a fixed port. A testing builder also copies launchSettings ports,
                // so clear both or every session binds the same endpoint. The endpoint also stays
                // unproxied: the proxy buffers the Aspire bridge's SSE command stream, so commands
                // from the brain to the AppHost would never arrive on a proxied endpoint. The silo
                // binds every interface because sandbox scripts dial back through the container
                // gateway (host.docker.internal), which on Linux is not the loopback.
                foreach (var http in resource.Annotations.OfType<EndpointAnnotation>())
                {
                    if (!string.Equals(http.Name, SiloHosts.HttpEndpointName, StringComparison.Ordinal)) { continue; }
                    http.Port = null;
                    http.TargetPort = null;
                    http.IsProxied = false;
                    http.TargetHost = "0.0.0.0";
                }
            }
            var containerBrowser = browserLocation == BrowserLocation.Container
                && builder.Resources.Any(r => r.Annotations.OfType<BrainBrowserAnnotation>().Any());
            if (containerBrowser) { BrowserContainer.AddTo(builder); }
            Advance("build");
            if (telemetryEnabled)
            {
                builder.Eventing.Subscribe<BeforeResourceStartedEvent>((started, _) =>
                {
                    if (started.Resource.Name == "aspire-dashboard")
                    {
                        started.Resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
                        {
                            context.EnvironmentVariables["Dashboard__TelemetryLimits__MaxLogCount"] = TestTelemetry.RetentionLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            context.EnvironmentVariables["Dashboard__TelemetryLimits__MaxTraceCount"] = TestTelemetry.RetentionLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            context.EnvironmentVariables["Dashboard__TelemetryLimits__MaxAttributeCount"] = "4096";
                            context.EnvironmentVariables["Dashboard__TelemetryLimits__MaxAttributeLength"] = "1048576";
                            context.EnvironmentVariables["Dashboard__TelemetryLimits__MaxSpanEventCount"] = "4096";
                        }));
                    }
                    return Task.CompletedTask;
                });
            }
            var app = await builder.BuildAsync(ct).ConfigureAwait(false);
            lifetime.Own("application", app);
            var session = new AspireTestSession(app, lifetime, options, selectedBrain, selectedHost.Name);
            Advance("start");
            await app.StartAsync(ct).WaitAsync(ct).ConfigureAwait(false);
            if (telemetryEnabled)
            {
                var dashboardUrl = await app.GetDashboardUrlAsync(ct).ConfigureAwait(false);
                var dashboardHttp = new HttpClient { BaseAddress = new Uri(dashboardUrl, "/") };
                dashboardHttp.DefaultRequestHeaders.Add("X-API-Key", builder.Configuration["AppHost:DashboardApiKey"]
                    ?? throw new InvalidOperationException("The dashboard API key was not configured."));
                lifetime.Own("dashboard-http", new AsyncAction(() => { dashboardHttp.Dispose(); return ValueTask.CompletedTask; }));
                var exporters = telemetryResources.Select(resource =>
                {
                    var client = app.CreateHttpClient(resource.Name, "http");
                    client.DefaultRequestHeaders.Add(TelemetryExportBarrier.CapabilityHeader, telemetryCapability);
                    lifetime.Own("exporter-http", new AsyncAction(() => { client.Dispose(); return ValueTask.CompletedTask; }));
                    return client;
                }).ToArray();
                session.Telemetry = new TestTelemetry(new DashboardTelemetryClient(dashboardHttp), async token =>
                {
                    using var suppressed = OpenTelemetry.SuppressInstrumentationScope.Begin();
                    return await Task.WhenAll(exporters.Select(async client =>
                    {
                        using var response = await client.PostAsync(TelemetryExportBarrier.Path, null, token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadFromJsonAsync<TelemetryExportReceipt>(token).ConfigureAwait(false)
                            ?? throw new InvalidDataException("An exporter returned no completion receipt.");
                    })).ConfigureAwait(false);
                });
            }
            Advance("readiness");
            foreach (var host in hosts)
            { await app.ResourceNotifications.WaitForResourceHealthyAsync(host.Name, ct).ConfigureAwait(false); }
            var browser = builder.Resources.SelectMany(r => r.Annotations.OfType<BrainBrowserAnnotation>()
                .Select(a => (r.Name, a.Endpoint, a.Path, a.ReadySelector))).ToArray();
            if (browser.Length > 1) { throw new InvalidOperationException("The AppHost declares multiple primary browser endpoints."); }
            if (browser.Length == 1)
            {
                await app.ResourceNotifications.WaitForResourceHealthyAsync(browser[0].Name, ct).ConfigureAwait(false);
                if (!browser[0].Path.StartsWith('/') || browser[0].Path.StartsWith("//", StringComparison.Ordinal)
                    || browser[0].Path.Contains('\\'))
                { throw new InvalidOperationException("Browser navigation must be relative to the advertised endpoint."); }
                // The session's kernel URL rides in the page address, so concurrent hosts
                // never share served state and the compiled bundle stays session-agnostic.
                session.BrowserEndpoint = new Uri(app.GetEndpoint(browser[0].Name, browser[0].Endpoint), browser[0].Path);
                session.BrowserReadySelector = browser[0].ReadySelector;
            }
            if (containerBrowser)
            {
                Advance("browser-container");
                await app.ResourceNotifications.WaitForResourceHealthyAsync(BrowserContainer.ResourceName, ct).ConfigureAwait(false);
                var server = app.GetEndpoint(BrowserContainer.ResourceName, BrowserContainer.EndpointName);
                session.BrowserServer = new UriBuilder(server) { Scheme = "ws" }.Uri;
            }
            Advance("client");
            await session.ConnectAsync(ct).ConfigureAwait(false);
            lifetime.Own("client", new AsyncAction(session.ReleaseClientAsync));
            session.HttpClient = app.CreateHttpClient(selectedHost.Name, SiloHosts.HttpEndpointName);
            lifetime.Own("http", new AsyncAction(() => { session.HttpClient.Dispose(); return ValueTask.CompletedTask; }));
            if (session.BrowserEndpoint is { } pageAddress)
            {
                var uiBase = Uri.EscapeDataString(session.HttpClient.BaseAddress!.GetLeftPart(UriPartial.Authority) + "/");
                session.BrowserEndpoint = new Uri(pageAddress
                    + (string.IsNullOrEmpty(pageAddress.Query) ? "?" : "&") + "uiBase=" + uiBase);
            }
            Console.WriteLine($"[aspire-boot] {stage}: {stageWatch.Elapsed.TotalSeconds:F1}s; total {bootWatch.Elapsed.TotalSeconds:F1}s");
            return session;
        }
        catch (Exception error)
        {
            Console.WriteLine($"[aspire-boot] failed in {stage} after {stageWatch.Elapsed.TotalSeconds:F1}s; total {bootWatch.Elapsed.TotalSeconds:F1}s");
            try { options.Diagnostics?.Invoke(new(stage, "Startup failed; releasing owned resources.")); } catch { }
            try { await lifetime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException($"Startup failed at '{stage}' and rollback failed.", error, cleanup); }
            throw new InvalidOperationException($"Test startup failed at '{stage}'.", error);
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var connection = _brain.ClusteringResourceName is { } clustering
            ? await App.GetConnectionStringAsync(clustering, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Missing brain clustering connection.")
            : null;
        var host = Host.CreateDefaultBuilder().ConfigureLogging(log => log.SetMinimumLevel(LogLevel.Warning))
            .UseOrleansClient(client =>
            {
                if (connection is not null)
                {
                    var settings = new ConfigurationBuilder().AddEnvironmentVariables().Build();
                    client.UseAzureStorageClustering(storage => storage.TableServiceClient = OrleansStorageHosting.ClusteringClient(settings, connection));
                }
                else
                {
                    var endpoint = App.GetEndpoint(_serverName, "orleans-gateway");
                    client.UseStaticClustering(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, endpoint.Port));
                }
                client.Configure<ClusterOptions>(cluster => { cluster.ClusterId = _brain.ClusterId; cluster.ServiceId = _brain.ServiceId; });
                client.AddIntoChatOS();
            }).Build();
        try { await host.StartAsync(ct).WaitAsync(ct).ConfigureAwait(false); _client = host; }
        catch { host.Dispose(); throw; }
    }

    private async ValueTask ReleaseClientAsync()
    {
        var host = Interlocked.Exchange(ref _client, null);
        if (host is null) { return; }
        using var deadline = new CancellationTokenSource(_options.CleanupTimeout);
        try
        {
            await ((IAsyncDisposable)host.Services.GetRequiredService<IIntoChatOSBrains>()).DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            await host.StopAsync(deadline.Token).ConfigureAwait(false);
        }
        finally { host.Dispose(); }
    }

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    private sealed class AsyncAction(Func<ValueTask> action) : IAsyncDisposable
    { public ValueTask DisposeAsync() => action(); }
}
