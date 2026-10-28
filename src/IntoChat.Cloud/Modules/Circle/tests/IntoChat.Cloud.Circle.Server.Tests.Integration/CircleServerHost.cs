using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Circles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

[assembly: AssemblyFixture(typeof(IntoChat.Cloud.Circle.Server.Tests.CircleServerHost))]

namespace IntoChat.Cloud.Circle.Server.Tests;

// The kernel composes the circle server module under Topology=cloud in 10-16; here a test module puts it on an
// in-process os brain directly. The invite redeemer is a scripted stand-in: the real one is covered by the circles
// module's own facts (10-03).
[DigitalBrain.Module(CircleServerModule.Id)]
public sealed class CircleServerHostModule : DigitalBrain.Module, IHttpModule
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
    public static Type Neurons => typeof(CircleServerModule);
    private readonly CircleServerModule _server = new();

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        _server.ConfigureServices(services);
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _server.Configure(endpoints);
        endpoints.ServiceProvider.GetService<ExtraRoutes>()?.Map(endpoints);
    }
}

// Routes a fact adds beside the module's own, standing in for later circle module endpoints.
internal sealed record ExtraRoutes(Action<IEndpointRouteBuilder> Map);

internal sealed record ScriptedInvite(string CircleId, string CircleName, string Role, string? AccountId, bool PendingAccount, bool Expired = false);

internal sealed class ScriptedRedeemer : ICircleInviteRedeemer
{
    private readonly ConcurrentDictionary<string, ScriptedInvite> _invites = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Machine, string Thumbprint)> _bound = new(StringComparer.Ordinal);

    public string Add(ScriptedInvite invite)
    {
        var code = InviteCode.Generate();
        _invites[code] = invite;
        return code;
    }

    public Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName)
    {
        if (!_invites.TryGetValue(code, out var invite)) { return Task.FromResult(RedeemOutcome.Rejected); }
        if (invite.Expired) { return Task.FromResult(RedeemOutcome.Expired); }
        var bound = _bound.GetOrAdd(code, (machineId, machineKeyThumbprint));
        var result = bound != (machineId, machineKeyThumbprint) ? RedeemResult.Rejected
            : _redeemed.TryAdd(code, 0) ? RedeemResult.Accepted : RedeemResult.Repeat;
        return Task.FromResult(result == RedeemResult.Rejected ? RedeemOutcome.Rejected
            : new RedeemOutcome(result, invite.CircleId, invite.CircleName, invite.Role, invite.AccountId, invite.PendingAccount));
    }

    private readonly ConcurrentDictionary<string, byte> _redeemed = new(StringComparer.Ordinal);
}

// The shared host: one brain for the facts that neither advance its clock nor depend on rate limits; those start their
// own CircleServer.
public sealed class CircleServerHost : BrainHostFixture
{
    internal ScriptedRedeemer Invites { get; } = new();
    internal FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    internal CircleServer Server => new(Brain, Invites, Clock, owns: false);

    protected override TestBrain Configure(TestBrain brain) => CircleServer.Compose(brain, Invites, Clock, CircleServer.Unlimited, CircleServer.Unlimited,
        endpoints => endpoints.MapGet(CircleServer.ApprovedProbe, () => Results.Ok()).RequireApprovedMachine());
}

internal sealed class CircleServer(ITestBrain brain, ScriptedRedeemer invites, FakeTimeProvider clock, bool owns) : IAsyncDisposable
{
    // Rate limits are raised unless a fact is about them, since every request here comes from loopback.
    public const int Unlimited = 10_000;
    public const string ApprovedProbe = "/circle/v1/probe";

    public ITestBrain Brain { get; } = brain;
    public ScriptedRedeemer Invites { get; } = invites;
    public FakeTimeProvider Clock { get; } = clock;

    public static async Task<CircleServer> StartAsync(CancellationToken ct, int perAddress = Unlimited, int perCircle = Unlimited)
    {
        var invites = new ScriptedRedeemer();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var brain = await Compose(TestBrain.Create(), invites, clock, perAddress, perCircle, null).StartAsync(ct);
        return new CircleServer(brain, invites, clock, owns: true);
    }

    public static TestBrain Compose(TestBrain brain, ScriptedRedeemer invites, FakeTimeProvider clock, int perAddress, int perCircle,
        Action<IEndpointRouteBuilder>? routes)
        => brain.WithModule<CircleServerHostModule>().ConfigureServices(services =>
        {
            services.RemoveAll<ICircleInviteRedeemer>();
            services.AddSingleton<ICircleInviteRedeemer>(invites);
            services.AddKeyedSingleton<TimeProvider>(CircleServerModule.ClockKey, clock);
            if (routes is not null) { services.AddSingleton(new ExtraRoutes(routes)); }
            services.Configure<CircleServerOptions>(options =>
            {
                options.EnrollPerAddress = perAddress;
                options.EnrollPerCircle = perCircle;
            });
        });

    public string Invite(string? circleId = null, bool pending = false, bool expired = false)
        => Invites.Add(new ScriptedInvite(circleId ?? "circle-" + Guid.NewGuid().ToString("N"), "Friends", "member", pending ? null : "account-1", pending, expired));

    public Machine NewMachine(string? id = null) => new(this, id ?? "machine-" + Guid.NewGuid().ToString("N"));

    public async ValueTask DisposeAsync()
    {
        if (owns) { await Brain.DisposeAsync(); }
    }
}

// A machine with its own P-256 key, speaking /circle/v1 the way the circle module does.
internal sealed class Machine(CircleServer server, string id) : IDisposable
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string Id { get; } = id;
    public string Spki => System.Buffers.Text.Base64Url.EncodeToString(Key.ExportSubjectPublicKeyInfo());

    private HttpClient Http => server.Brain.HttpClient;

    public Task<HttpResponseMessage> Enroll(string code, string? spki = null)
        => Http.PostAsJsonAsync(CircleRoutes.Enroll, new EnrollRequest(code, spki ?? Spki, Id, "desk", "test"), CircleJsonContext.Default.EnrollRequest);

    public async Task<EnrollResponse> EnrollOk(string code)
    {
        using var response = await Enroll(code);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(CircleJsonContext.Default.EnrollResponse))!;
    }

    public async Task<string> Proof(HttpMethod method, string path, string token, DateTimeOffset? at = null, string? nonce = null, ECDsa? key = null)
        => await MachineProof.CreateAsync(data => ValueTask.FromResult((key ?? Key).SignData(data.Span, HashAlgorithmName.SHA256)),
            method.Method, new Uri(Http.BaseAddress!, path), token, at ?? server.Clock.GetUtcNow(), nonce ?? MachineProof.NewNonce());

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? accessToken, string? proof = null, HttpContent? content = null, bool withProof = true)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (accessToken is not null) { request.Headers.Authorization = new("Bearer", accessToken); }
        if (withProof && accessToken is not null) { request.Headers.Add(CircleRoutes.MachineProofHeader, proof ?? await Proof(method, path, accessToken)); }
        return await Http.SendAsync(request);
    }

    public async Task<HttpResponseMessage> Refresh(string refreshToken, string? proof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CircleRoutes.Refresh)
        {
            Content = JsonContent.Create(new RefreshRequest(refreshToken), CircleJsonContext.Default.RefreshRequest),
        };
        request.Headers.Add(CircleRoutes.MachineProofHeader, proof ?? await Proof(HttpMethod.Post, CircleRoutes.Refresh, refreshToken));
        return await Http.SendAsync(request);
    }

    public void Dispose() => Key.Dispose();
}
