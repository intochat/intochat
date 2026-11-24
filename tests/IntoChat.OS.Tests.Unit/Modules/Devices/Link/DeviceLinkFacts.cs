using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

public sealed class DeviceLinkFacts
{
    [Fact]
    public async Task JoiningSendsTheKeyAndKeepsSecretsOutOfState()
    {
        await using var harness = await Open(owner: "ada");

        LinkStatus status;
        using (harness.AsPerson("ada"))
        {
            status = await harness.Link.Join(new Uri("https://cloud.test"), Code.New());
        }

        Assert.Equal(LinkState.Connected, status.State);
        Assert.Equal(new Uri("https://cloud.test"), status.Server);

        var enroll = Assert.Single(harness.Server.Requests, r => r.Path == CircleRoutes.Enroll);
        var request = enroll.Body(DeviceWireJson.Default.EnrollRequest);
        Assert.Equal(91, Base64Url.DecodeFromChars(request.MachinePublicKeySpki).Length);
        Assert.Equal(harness.Machine.Value, request.MachineId);

        var ct = TestContext.Current.CancellationToken;
        var inspected = await ((IBrainObserver)harness.Os).State(harness.LinkId, ct);
        Assert.NotNull(inspected);
        var stateJson = inspected!.Value.GetRawText();
        Assert.DoesNotContain("refresh", stateJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.Server.IssuedRefreshToken, stateJson, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Server.IssuedAccessToken, stateJson, StringComparison.Ordinal);

        Assert.True(File.Exists(harness.CredentialsFile));
        var stored = await File.ReadAllBytesAsync(harness.CredentialsFile, ct);
        Assert.NotEqual(Encoding.UTF8.GetBytes(harness.Server.IssuedRefreshToken), stored);
        Assert.DoesNotContain(harness.Server.IssuedRefreshToken, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCallCarriesAProofThatVerifies()
    {
        await using var harness = await Open(owner: "ada");

        using (harness.AsPerson("ada"))
        {
            await harness.Link.Join(new Uri("https://cloud.test"), Code.New());
            var status = await harness.Link.Status();
            Assert.Equal(LinkState.Connected, status.State);
        }

        var nonEnroll = harness.Server.Requests.Where(r => r.Path != CircleRoutes.Enroll).ToArray();
        Assert.NotEmpty(nonEnroll);
        Assert.All(nonEnroll, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.Bearer));
            var result = MachineProof.Verify(
                harness.Server.Spki,
                r.Header(CircleRoutes.MachineProofHeader),
                r.Method,
                r.Url,
                r.Bearer!,
                harness.Server.Now,
                new NoReplay());
            Assert.True(result.IsValid, $"Proof failed on {r.Method} {r.Path}: {result.Error}");
        });
    }

    [Fact]
    public async Task AnExpiringTokenIsRefreshedFirst()
    {
        await using var harness = await Open(owner: "ada");

        using (harness.AsPerson("ada"))
        {
            await harness.Link.Join(new Uri("https://cloud.test"), Code.New());
        }

        var firstAccess = harness.Server.IssuedAccessToken;
        harness.Os.Clock.Advance(harness.Server.AccessLifetime - TimeSpan.FromSeconds(30));

        LinkStatus status;
        using (harness.AsPerson("ada"))
        {
            status = await harness.Link.Status();
        }

        Assert.Equal(LinkState.Connected, status.State);
        var paths = harness.Server.Requests.Select(r => r.Path).ToArray();
        Assert.Equal([CircleRoutes.Enroll, CircleRoutes.Refresh, CircleRoutes.Status], paths);

        var statusCall = harness.Server.Requests.Last();
        Assert.NotEqual(firstAccess, statusCall.Bearer);
        Assert.Equal(harness.Server.IssuedAccessToken, statusCall.Bearer);

        var nonEnroll = harness.Server.Requests.Where(r => r.Path != CircleRoutes.Enroll).ToArray();
        Assert.All(nonEnroll, r =>
            Assert.True(MachineProof.Verify(
                harness.Server.Spki,
                r.Header(CircleRoutes.MachineProofHeader),
                r.Method,
                r.Url,
                r.Bearer!,
                r.At,
                new NoReplay()).IsValid));
    }

    [Fact]
    public async Task A401OnRefreshMarksTheLinkRevoked()
    {
        await using var harness = await Open(owner: "ada");

        using (harness.AsPerson("ada"))
        {
            await harness.Link.Join(new Uri("https://cloud.test"), Code.New());
        }

        Assert.True(File.Exists(harness.CredentialsFile));
        harness.Server.RejectRefreshWith401 = true;
        harness.Os.Clock.Advance(harness.Server.AccessLifetime - TimeSpan.FromSeconds(30));

        LinkStatus status;
        using (harness.AsPerson("ada"))
        {
            status = await harness.Link.Status();
        }

        Assert.Equal(LinkState.Revoked, status.State);
        Assert.False(File.Exists(harness.CredentialsFile));
    }

    [Fact]
    public async Task OnlyTheOwnerAsAPersonJoinsOrLeaves()
    {
        await using var harness = await Open(owner: "ada");
        var server = new Uri("https://cloud.test");

        using (harness.AsApp("ada"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.Link.Join(server, Code.New()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Link.Leave);
        }

        using (harness.AsAgent("ada"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.Link.Join(server, Code.New()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Link.Leave);
        }

        using (harness.AsPerson("eve", brain: "ada"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.Link.Join(server, Code.New()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Link.Leave);
        }

        using (harness.AsPerson("ada", brain: "ben"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.Link.Join(server, Code.New()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(harness.Link.Leave);
        }
    }

    [Fact]
    public async Task LeavingNotifiesCloudDeletesCredentialsAndResetsState()
    {
        await using var harness = await Open(owner: "ada");

        using (harness.AsPerson("ada"))
        {
            await harness.Link.Join(new Uri("https://cloud.test"), Code.New());
            await harness.Link.Leave();
            var status = await harness.Link.Status();
            Assert.Equal(LinkState.NotJoined, status.State);
        }

        Assert.Contains(harness.Server.Requests, r => r.Path == CircleRoutes.Leave);
        Assert.False(File.Exists(harness.CredentialsFile));
    }

    [Fact]
    public async Task DeviceWorkerSeeing401MarksLinkStatusRevokedInsteadOfNotJoined()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Open(owner: "ada");

        using (harness.AsPerson("ada"))
        {
            var joined = await harness.Link.Join(new Uri("https://cloud.test"), Code.New());
            Assert.Equal(LinkState.Connected, joined.State);
        }

        Assert.True(File.Exists(harness.CredentialsFile));
        harness.Server.RejectAllAuthenticatedWith401 = true;

        var worker = new DeviceWorker(
            harness.Cloud,
            harness.Credentials,
            [],
            harness.Machine,
            NullLogger<DeviceWorker>.Instance,
            harness.Os.Clock,
            () => "vlad-pc");

        await Assert.ThrowsAsync<DeviceLinkRevokedException>(() => worker.Tick(ct));
        Assert.False(File.Exists(harness.CredentialsFile));

        LinkStatus status;
        using (harness.AsPerson("ada"))
        {
            status = await harness.Link.Status();
        }

        Assert.Equal(LinkState.Revoked, status.State);
        Assert.False(File.Exists(harness.CredentialsFile));
        var inspected = await ((IBrainObserver)harness.Os).State(harness.LinkId, ct);
        Assert.NotNull(inspected);
        Assert.DoesNotContain(harness.Server.IssuedRefreshToken, inspected!.Value.GetRawText(), StringComparison.Ordinal);
    }

    private static Task<Harness> Open(string owner)
    {
        TestOS? os = null;
        var tempRoot = new TempDirectory("intochat-device-link-facts-");
        var root = tempRoot.Path;
        var machine = new MachineId(Path.Combine(root, "machine-id"));
        var credentialsFile = Path.Combine(root, "devices", "link.bin");
        var protector = new XorTestProtector();
        var credentials = new DeviceCredentials(protector, credentialsFile);
        var server = new FakeCloudServer(() => os!.Clock.GetUtcNow());
        var http = new HttpClient(server);

        var services = new ServiceCollection();
        services.AddSingleton(TestEdge.Authority);
        services.AddSingleton<IIdentityClient>(new StaticOwnerIdentity(owner, owner));
        services.AddSingleton(machine);
        services.AddSingleton(credentials);
        services.AddSingleton<TimeProvider>(_ => os!.Clock);
        services.AddSingleton(sp => new CloudClient(http, credentials, sp.GetRequiredService<TimeProvider>()));
        var provider = services.BuildServiceProvider();

        os = TestOS.Local(o => o.Services = provider);
        os.Register<DeviceLinkNeuron>();

        var linkId = new NeuronId(new BrainId(owner), "devices.link", "device-link");
        var link = os.Get<IDeviceLink>(linkId);
        return Task.FromResult(new Harness(os, provider, http, server, machine, linkId, link, credentialsFile, tempRoot));
    }

    private sealed class Harness(
        TestOS os,
        ServiceProvider services,
        HttpClient http,
        FakeCloudServer server,
        MachineId machine,
        NeuronId linkId,
        IDeviceLink link,
        string credentialsFile,
        TempDirectory tempRoot) : IAsyncDisposable
    {
        public TestOS Os => os;
        public FakeCloudServer Server => server;
        public MachineId Machine => machine;
        public NeuronId LinkId => linkId;
        public IDeviceLink Link => link;
        public string CredentialsFile => credentialsFile;
        public DeviceCredentials Credentials => services.GetRequiredService<DeviceCredentials>();
        public CloudClient Cloud => services.GetRequiredService<CloudClient>();

        public IDisposable AsPerson(string account, string? brain = null)
            => TestEdge.Authority.Enter(TestEdge.Authority.Person(account, account, new BrainId(brain ?? account)));

        public IDisposable AsApp(string brain)
            => TestEdge.Authority.Enter(TestEdge.Authority.App($"{brain}/apps/test", new Actor.Person(brain, brain), new BrainId(brain)));

        public IDisposable AsAgent(string brain)
            => TestEdge.Authority.Enter(TestEdge.Authority.Agent(new Actor.Person(brain, brain), new BrainId(brain)));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await os.DisposeAsync();
                await services.DisposeAsync();
                http.Dispose();
            }
            finally
            {
                tempRoot.Dispose();
            }
        }
    }

    private sealed class XorTestProtector : IMachineKeyProtector
    {
        private const byte Mask = 0x5A;
        public byte[] Protect(byte[] key) => [.. key.Select(b => (byte)(b ^ Mask))];
        public byte[] Unprotect(byte[] stored) => [.. stored.Select(b => (byte)(b ^ Mask))];
    }

    private sealed class NoReplay : INonceCache
    {
        public bool TryAdd(string nonce, DateTimeOffset expiresAt) => true;
    }

    private sealed record RecordedRequest(
        string Method,
        Uri Url,
        string Path,
        string? Bearer,
        Dictionary<string, string> Headers,
        string RawBody,
        DateTimeOffset At)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";

        public T Body<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
            => JsonSerializer.Deserialize(RawBody, typeInfo)!;
    }

    private sealed class FakeCloudServer(Func<DateTimeOffset> clock) : HttpMessageHandler
    {
        private int _tokenGeneration;
        public TimeSpan AccessLifetime { get; } = TimeSpan.FromMinutes(15);
        public List<RecordedRequest> Requests { get; } = [];
        public byte[] Spki { get; private set; } = [];
        public string MachineId { get; private set; } = "";
        public string IssuedAccessToken { get; private set; } = "";
        public string IssuedRefreshToken { get; private set; } = "";
        public bool RejectRefreshWith401 { get; set; }
        public bool RejectAllAuthenticatedWith401 { get; set; }
        public DateTimeOffset Now => clock();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var rawBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }
            var bearer = request.Headers.Authorization is { Scheme: "Bearer", Parameter: { Length: > 0 } p } ? p : null;
            var url = request.RequestUri!;
            Requests.Add(new RecordedRequest(request.Method.Method, url, url.AbsolutePath, bearer, headers, rawBody, Now));

            if (url.AbsolutePath == CircleRoutes.Enroll)
            {
                var enroll = JsonSerializer.Deserialize(rawBody, DeviceWireJson.Default.EnrollRequest)!;
                Spki = Base64Url.DecodeFromChars(enroll.MachinePublicKeySpki);
                MachineId = enroll.MachineId;
                return IssueTokens();
            }

            if (url.AbsolutePath == CircleRoutes.Refresh)
            {
                if (RejectRefreshWith401 || RejectAllAuthenticatedWith401)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                return IssueTokens();
            }

            if (RejectAllAuthenticatedWith401)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (url.AbsolutePath == CircleRoutes.Status)
            {
                var status = new CircleStatusResponse("ada", "Ada", "owner", false, false, MachineId);
                return JsonResponse(JsonSerializer.Serialize(status, DeviceWireJson.Default.CircleStatusResponse));
            }

            if (url.AbsolutePath == CircleRoutes.Leave)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage IssueTokens()
        {
            var gen = ++_tokenGeneration;
            IssuedAccessToken = $"cred1.access-secret-{gen}";
            IssuedRefreshToken = $"cred1.refresh-secret-{gen}";
            var now = Now;
            var response = new EnrollResponse(
                "cred1",
                IssuedAccessToken,
                now + AccessLifetime,
                IssuedRefreshToken,
                now + TimeSpan.FromDays(30),
                new CircleInfo("ada", "Ada", "owner"),
                false,
                new CircleEndpoints(CircleRoutes.Journal, CircleRoutes.Store, CircleRoutes.Files));
            return JsonResponse(JsonSerializer.Serialize(response, DeviceWireJson.Default.EnrollResponse));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            return msg;
        }
    }

    private sealed class StaticOwnerIdentity(string brainId, string ownerAccount) : IIdentityClient
    {
        public Task<Account?> OwnerOfBrain(string brain)
            => Task.FromResult<Account?>(string.Equals(brain, brainId, StringComparison.Ordinal)
                ? new Account { AccountId = ownerAccount, Name = ownerAccount, OwnerPrincipalId = ownerAccount }
                : null);

        public Task<Account?> ReadAccount(string accountId) => throw new NotSupportedException();
        public Task EnsureAccount(Account account) => throw new NotSupportedException();
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind) => throw new NotSupportedException();
        public Task<BrainProvision> EnsureBrain(Member owner) => throw new NotSupportedException();
        public Task<BrainProvision> ProvisionOwner(Member owner) => throw new NotSupportedException();
        public Task<bool> IsRegistered(string principalId) => throw new NotSupportedException();
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => throw new NotSupportedException();
        public Task<BrainProvision?> CompleteRegistration(string principalId) => throw new NotSupportedException();
        public Task<BrainEntry[]> BrainsOf(string principalId) => throw new NotSupportedException();
        public Task<BrainKind> KindOf(string brainScopeId) => throw new NotSupportedException();
        public Task<bool> BrainExists(string brainScopeId) => throw new NotSupportedException();
        public Task<Member?> Membership(string brainScopeId, string principalId) => throw new NotSupportedException();
        public Task<Member?> EnsureMember(Member member) => throw new NotSupportedException();
        public Task RemoveMember(string brainScopeId, string accountId) => throw new NotSupportedException();
        public Task RemovePrincipalMember(string brainScopeId, string principalId) => throw new NotSupportedException();
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request) => throw new NotSupportedException();
        public Task<Grant[]> ListGrants(string brainScopeId) => throw new NotSupportedException();
        public Task<Grant> GrantAsOwner(string brainId, Grant grant) => throw new NotSupportedException();
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => throw new NotSupportedException();
        public Task RevokeAsOwner(string brainId, string grantId) => throw new NotSupportedException();
        public Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode) => throw new NotSupportedException();
    }
}
