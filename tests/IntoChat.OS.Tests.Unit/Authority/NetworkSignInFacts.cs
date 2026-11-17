using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Authority;

// Sign-in on a kernel that listens beyond loopback, in every topology: it refuses to start without a sign-in module unless the
// bootstrap credential or the explicit opt-in is configured, and the built-in registration is closed there whatever is configured.
// No fact opens a socket: the host runs on a server that binds nothing, so a network address is only ever configuration here.
public sealed class NetworkSignInFacts
{
    private const string Network = "http://0.0.0.0:8080";
    private const string Loopback = "http://127.0.0.1:8080";
    private const string UsernameKey = "IntoChatOS:Auth:Username";
    private const string PasswordKey = "IntoChatOS:Auth:Password";
    private const string AllowKey = "IntoChatOS:Auth:AllowOpenOnNetwork";
    private const string Secret = "bootstrap-password-must-not-leak";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Empty stands for an unset topology, which means local.
    public static TheoryData<string> Topologies => new() { "", "local", "local+cloud", "cloud" };

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task OnANetworkAddress_WithNoSignInModule_NoCredentialAndNoOptIn_TheKernelRefusesToStart(string topology)
    {
        await using var kernel = TestKernel.Build(Settings(topology, Network));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.App.StartAsync(Ct));

        Assert.Contains(Network, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(UsernameKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(PasswordKey, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(AllowKey + "=true", refusal.Message, StringComparison.Ordinal);
        Assert.False(kernel.Server.Started, "The server was started before the refusal.");
    }

    [Fact]
    public async Task AContainerIsNotExempt()
    {
        var settings = Settings("local", Network);
        settings["DOTNET" + "_RUNNING_IN_CONTAINER"] = "true";
        await using var kernel = TestKernel.Build(settings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.App.StartAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task TheBootstrapCredential_LetsItStart_AndRegistrationStaysClosed(string topology)
    {
        var settings = Settings(topology, Network);
        settings[UsernameKey] = "operator";
        settings[PasswordKey] = Secret;
        await using var kernel = TestKernel.Build(settings);

        await kernel.App.StartAsync(Ct);

        Assert.False(kernel.Exit.Failed);
        Assert.DoesNotContain("/identity/register", kernel.Routes());
        // The one owner still signs in: the password route stays, the Basic credential is the edge's.
        Assert.Contains("/identity/login", kernel.Routes());
        Assert.Equal(StatusCodes.Status401Unauthorized, await kernel.Server.Send("POST", "/identity/register"));
    }

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task TheOptIn_LetsItStart_AndRegistrationStaysClosed(string topology)
    {
        var settings = Settings(topology, Network);
        settings[AllowKey] = "true";
        await using var kernel = TestKernel.Build(settings);

        await kernel.App.StartAsync(Ct);

        Assert.False(kernel.Exit.Failed);
        Assert.DoesNotContain("/identity/register", kernel.Routes());
        Assert.Equal(StatusCodes.Status401Unauthorized, await kernel.Server.Send("POST", "/identity/register"));
    }

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task OnLoopback_NothingChanges(string topology)
    {
        await using var kernel = TestKernel.Build(Settings(topology, Loopback));

        await kernel.App.StartAsync(Ct);

        Assert.False(kernel.Exit.Failed);
        Assert.Contains("/identity/register", kernel.Routes());
        Assert.Contains("/identity/login", kernel.Routes());
        // Still anonymous: the edge lets it through to the route, which answers a request with no body itself.
        Assert.NotEqual(StatusCodes.Status401Unauthorized, await kernel.Server.Send("POST", "/identity/register"));
    }

    [Fact]
    public async Task WithNoListenerConfigured_NothingChanges()
    {
        await using var kernel = TestKernel.Build(Settings(null, urls: null));

        await kernel.App.StartAsync(Ct);

        Assert.False(kernel.Exit.Failed);
        Assert.Contains("/identity/register", kernel.Routes());
    }

    [Theory]
    [MemberData(nameof(Topologies))]
    public async Task ASignInModule_KeepsTheKernelAsItWas(string topology)
    {
        await using var kernel = TestKernel.Build(Settings(topology, Network), SignInModule);

        await kernel.App.StartAsync(Ct);

        Assert.False(kernel.Exit.Failed);
        Assert.DoesNotContain("/identity/register", kernel.Routes());
        Assert.DoesNotContain("/identity/login", kernel.Routes());
        Assert.Equal(StatusCodes.Status401Unauthorized, await kernel.Server.Send("POST", "/identity/register"));
    }

    // The cloud's own registration, as the kernel's Program applies it under the cloud topology.
    [Theory]
    [InlineData(Network)]
    [InlineData(Loopback)]
    public async Task TheCloudComposition_StartsAndServesAsBefore(string urls)
    {
        await using var kernel = TestKernel.Build(Settings("cloud", urls), CloudHost.Register);

        await kernel.App.StartAsync(Ct);

        Assert.False(kernel.Exit.Failed);
        Assert.DoesNotContain("/identity/register", kernel.Routes());
        Assert.DoesNotContain("/identity/login", kernel.Routes());
        Assert.Equal(StatusCodes.Status401Unauthorized, await kernel.Server.Send("POST", "/identity/register"));
        Assert.Equal(StatusCodes.Status401Unauthorized, await kernel.Server.Send("POST", "/identity/login"));
    }

    // Switching the built-in routes off by configuration is not a sign-in module: nothing took sign-in over.
    [Fact]
    public async Task TheOptionAlone_IsNotASignInModule()
    {
        var settings = Settings("cloud", Network);
        settings[IdentityHostOptions.Key(nameof(IdentityHostOptions.BuiltInPasswordEndpoints))] = "false";
        await using var kernel = TestKernel.Build(settings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.App.StartAsync(Ct));
    }

    // A listener set in code is invisible to configuration: the addresses the server really bound are checked after the bind.
    [Fact]
    public async Task AListenerBoundInCodeBeyondLoopback_StopsTheKernel_WhenTheRuleIsNotMet()
    {
        await using var kernel = TestKernel.Build(Settings("cloud", Loopback), boundInCode: [Network]);

        await kernel.App.StartAsync(Ct);

        Assert.True(kernel.Exit.Failed);
        Assert.True(kernel.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task AListenerBoundInCodeBeyondLoopback_StopsTheKernel_WhileRegistrationIsMapped()
    {
        var settings = Settings("cloud", Loopback);
        settings[UsernameKey] = "operator";
        settings[PasswordKey] = Secret;
        await using var kernel = TestKernel.Build(settings, boundInCode: [Network]);

        await kernel.App.StartAsync(Ct);

        Assert.Contains("/identity/register", kernel.Routes());
        Assert.True(kernel.Exit.Failed);
        Assert.True(kernel.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task AListenerBoundInCodeBeyondLoopback_IsFine_UnderASignInModule()
    {
        await using var kernel = TestKernel.Build(Settings("cloud", Loopback), SignInModule, boundInCode: [Network]);

        await kernel.App.StartAsync(Ct);

        Assert.False(kernel.Exit.Failed);
        Assert.False(kernel.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:8080")]
    [InlineData("urls", "http://127.0.0.1:5080;http://192.168.1.20:5080")]
    [InlineData("ASPNETCORE" + "_URLS", "http://+:8080")]
    [InlineData("DOTNET" + "_URLS", "http://[::]:8080")]
    [InlineData("urls", "http://brain.example.com:8080")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://*:8080")]
    [InlineData("ASPNETCORE" + "_HTTP_PORTS", "8080")]
    [InlineData("HTTPS_PORTS", "8443")]
    [InlineData("IntoChatOS:Bind", "0.0.0.0")]
    public void EveryWayToListenBeyondLoopback_IsSeen(string key, string value)
        => Assert.NotEmpty(NetworkSignIn.Exposed(Configuration(new() { [key] = value })));

    [Theory]
    [InlineData("urls", "http://127.0.0.1:8080")]
    [InlineData("urls", "http://localhost:5080;https://[::1]:5443")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://localhost:8080")]
    [InlineData("IntoChatOS:Bind", "127.0.0.1")]
    [InlineData("IntoChatOS:Home", "anything")]
    public void Loopback_IsNotExposure(string key, string value)
        => Assert.Empty(NetworkSignIn.Exposed(Configuration(new() { [key] = value })));

    [Fact]
    public void TheDecision_IsOneTable()
    {
        var bare = Configuration([]);
        var credential = Configuration(new() { [UsernameKey] = "operator", [PasswordKey] = Secret });
        var optIn = Configuration(new() { [AllowKey] = "true" });
        string[] loopback = [];
        string[] network = [Network];

        // Loopback: as it was, whatever else is configured.
        Assert.Equal(new NetworkSignIn.Verdict(true, true, null), NetworkSignIn.Decide(loopback, signInModule: false, builtInPasswordEndpoints: true, bare));
        Assert.Equal(new NetworkSignIn.Verdict(true, false, null), NetworkSignIn.Decide(loopback, signInModule: true, builtInPasswordEndpoints: false, bare));
        // Network with a sign-in module: as it was.
        Assert.Equal(new NetworkSignIn.Verdict(true, false, null), NetworkSignIn.Decide(network, signInModule: true, builtInPasswordEndpoints: false, bare));
        // Network without one: the two ways out start it, and registration is closed in each.
        Assert.Equal(new NetworkSignIn.Verdict(true, false, null), NetworkSignIn.Decide(network, signInModule: false, builtInPasswordEndpoints: true, credential));
        Assert.Equal(new NetworkSignIn.Verdict(true, false, null), NetworkSignIn.Decide(network, signInModule: false, builtInPasswordEndpoints: true, optIn));
        var refused = NetworkSignIn.Decide(network, signInModule: false, builtInPasswordEndpoints: true, bare);
        Assert.False(refused.Starts);
        Assert.False(refused.OpenRegistration);
        Assert.NotNull(refused.Refusal);
        // Half a credential is none.
        Assert.False(NetworkSignIn.Decide(network, false, true, Configuration(new() { [UsernameKey] = "operator" })).Starts);
    }

    private static Dictionary<string, string?> Settings(string? topology, string? urls)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [AuthOptions.PostureKey] = "Secured" };
        if (!string.IsNullOrEmpty(topology)) { settings[IntoChatOSNames.TopologyKey] = topology; }
        if (urls is not null) { settings["urls"] = urls; }
        return settings;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    // What a sign-in module leaves on the host: it takes the password routes away and binds the sessions over its own port.
    private static void SignInModule(IServiceCollection services)
    {
        services.PostConfigure<IdentityHostOptions>(identity => identity.BuiltInPasswordEndpoints = false);
        services.AddSingleton<IIdentitySessionPort, ModuleSessions>();
    }

    private sealed class TestKernel(WebApplication app, SocketlessServer server, RecordingExit exit) : IAsyncDisposable
    {
        public WebApplication App => app;
        public SocketlessServer Server => server;
        public RecordingExit Exit => exit;

        public static TestKernel Build(Dictionary<string, string?> settings, Action<IServiceCollection>? compose = null, string[]? boundInCode = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
                ApplicationName = typeof(NetworkSignInFacts).Assembly.GetName().Name,
                ContentRootPath = Path.GetTempPath(),
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            // The process environment must not decide what these hosts listen on.
            foreach (var key in (string[])["urls", "ASPNETCORE" + "_URLS", "DOTNET" + "_URLS", "ASPNETCORE" + "_HTTP_PORTS", "ASPNETCORE" + "_HTTPS_PORTS", "DOTNET" + "_HTTP_PORTS", "DOTNET" + "_HTTPS_PORTS", "HTTP_PORTS", "HTTPS_PORTS"])
            { settings.TryAdd(key, null); }
            builder.Configuration.AddInMemoryCollection(settings);
            var server = new SocketlessServer(boundInCode ?? []);
            var exit = new RecordingExit();
            builder.Services.AddSingleton<OpenPostureLoopback.OpenPostureExit>(exit);
            // The kernel registers these before the modules and the platform (AddIntoChatOSServer).
            compose?.Invoke(builder.Services);
            builder.Services.AddIdentity();
            builder.Services.AddSingleton<IServer>(server);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAccountSession();
            IdentityEndpoints.Map(app);
            return new TestKernel(app, server, exit);
        }

        public string[] Routes()
            => [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Select(route => route.RoutePattern.RawText ?? "")];

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private sealed class RecordingExit : OpenPostureLoopback.OpenPostureExit
    {
        public bool Failed { get; private set; }
        public override void Fail() => Failed = true;
    }

    // Serves the application in memory. Its address list starts with what the host bound in code, as Kestrel's does.
    private sealed class SocketlessServer : IServer
    {
        private Func<string, string, Task<int>>? _send;

        public SocketlessServer(string[] boundInCode)
        {
            var addresses = new BoundAddresses();
            foreach (var address in boundInCode) { addresses.Addresses.Add(address); }
            Features.Set<IServerAddressesFeature>(addresses);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();
        public bool Started => _send is not null;

        public Task<int> Send(string method, string path)
            => (_send ?? throw new InvalidOperationException("The server has not started."))(method, path);

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
        {
            _send = async (method, path) =>
            {
                var features = new FeatureCollection();
                features.Set<IHttpRequestFeature>(new HttpRequestFeature { Method = method, Path = path, Scheme = "http", Protocol = "HTTP/1.1" });
                var response = new HttpResponseFeature();
                features.Set<IHttpResponseFeature>(response);
                features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
                var context = application.CreateContext(features);
                try
                {
                    await application.ProcessRequestAsync(context);
                    application.DisposeContext(context, null);
                }
                catch (Exception failure)
                {
                    application.DisposeContext(context, failure);
                    throw;
                }
                return response.StatusCode;
            };
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }

        private sealed class BoundAddresses : IServerAddressesFeature
        {
            ICollection<string> IServerAddressesFeature.Addresses => Addresses;
            public List<string> Addresses { get; } = [];
            public bool PreferHostingUrls { get; set; }
        }
    }

    private sealed class ModuleSessions : IIdentitySessionPort
    {
        public Task<SessionIssued> Issue(CapabilityCaller caller, SessionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task End(CapabilityCaller caller, string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Member?> Validate(string sessionId, CancellationToken cancellationToken) => Task.FromResult<Member?>(null);
        public IdentityProviderDescriptor Describe() => throw new NotSupportedException();
    }
}
