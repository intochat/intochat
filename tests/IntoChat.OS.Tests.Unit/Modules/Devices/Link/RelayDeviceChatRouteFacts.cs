using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

// Owner decision D5: device chat goes from the OS through the cloud relay over HTTP, signed with the device link's machine
// credential. The relay names the credential's brain; the route offers nothing to another brain and never offers this machine.
public sealed class RelayDeviceChatRouteFacts
{
    private static readonly BrainId Ada = new("ada");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AdvertisedModel Llama { get; } = new("llama3", "Llama 3", "llama", Abilities.Text | Abilities.Tools);

    [Fact]
    public async Task WithoutALink_NothingIsAskedAndNothingOffered()
    {
        await using var world = await World.Open(linked: false);

        Assert.Empty(await world.Route.Models(Ada, Ct));
        Assert.Null(await world.Route.Chat(Ada, new DeviceModelRef("pc2", new ModelId("llama3")), Ct));
        Assert.Empty(world.Relay.Requests);
    }

    [Fact]
    public async Task TheBrainsOtherOnlineDevicesAreOffered_SignedWithTheMachineCredential_WithoutThisMachine()
    {
        await using var world = await World.Open();
        world.Relay.Devices = new BrainDevicesResponse("ada", [new(world.ThisMachine, "self", [Llama]), new("m2", "pc2", [Llama])]);

        var cards = await world.Route.Models(Ada, Ct);

        var card = Assert.Single(cards);
        Assert.Equal("device:pc2/llama3", card.Id.Value);
        Assert.Equal((Where.Device, AiProvider.Device), (card.Where, card.Provider));
        Assert.False(card.Can.HasFlag(Abilities.Tools));
        var request = Assert.Single(world.Relay.Requests);
        Assert.Equal(DeviceRoutes.Catalog, request.Path);
        Assert.Equal("?brain=ada", request.Query);
        Assert.Equal(world.AccessToken, request.Bearer);
        Assert.False(string.IsNullOrEmpty(request.Proof));
    }

    [Fact]
    public async Task TheRelaysAnswerForAnotherBrain_OffersNothing()
    {
        await using var world = await World.Open();
        world.Relay.Devices = new BrainDevicesResponse("eve", [new("m2", "pc2", [Llama])]);

        Assert.Empty(await world.Route.Models(Ada, Ct));
        Assert.Null(await world.Route.Chat(Ada, new DeviceModelRef("pc2", new ModelId("llama3")), Ct));
    }

    [Fact]
    public async Task TheAnswerIsKeptBriefly()
    {
        await using var world = await World.Open();
        world.Relay.Devices = new BrainDevicesResponse("ada", [new("m2", "pc2", [Llama])]);

        await world.Route.Models(Ada, Ct);
        await world.Route.Models(Ada, Ct);
        Assert.Single(world.Relay.Requests);

        world.Clock.Advance(RelayDeviceChatRoute.CacheFor);
        await world.Route.Models(Ada, Ct);
        Assert.Equal(2, world.Relay.Requests.Count);
    }

    [Fact]
    public async Task AChatStreamsThroughTheRelay_ForTheBrainAndDeviceItNames()
    {
        await using var world = await World.Open();
        world.Relay.Devices = new BrainDevicesResponse("ada", [new("m2", "pc2", [Llama])]);
        world.Relay.ChatLines = [new DeviceChunk("Hel"), new DeviceChunk("lo"), new DeviceChunk(null, Done: true, InputTokens: 3, OutputTokens: 2)];

        Assert.Null(await world.Route.Chat(Ada, new DeviceModelRef("pc2", new ModelId("other")), Ct));
        Assert.Null(await world.Route.Chat(Ada, new DeviceModelRef("pc9", new ModelId("llama3")), Ct));
        using var client = await world.Route.Chat(Ada, new DeviceModelRef("pc2", new ModelId("llama3")), Ct);
        var response = await client!.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Instructions = "be brief" }, Ct);

        Assert.Equal("Hello", response.Text);
        Assert.Equal(5, response.Usage?.TotalTokenCount);
        var sent = JsonSerializer.Deserialize(world.Relay.Requests.Single(r => r.Path == DeviceRoutes.Chat).Body, DeviceWireJson.Default.BrainChatRequest)!;
        Assert.Equal(("ada", "pc2", "llama3"), (sent.Brain, sent.Device, sent.Job.Model));
        Assert.Equal([("system", "be brief"), ("user", "hi")], sent.Job.Messages.Select(m => (m.Role, m.Text)));
    }

    [Fact]
    public async Task ADeviceErrorOrARefusedChat_IsADeviceUnavailable()
    {
        await using var world = await World.Open();
        world.Relay.Devices = new BrainDevicesResponse("ada", [new("m2", "pc2", [Llama])]);
        var model = new DeviceModelRef("pc2", new ModelId("llama3"));

        world.Relay.ChatLines = [new DeviceChunk(null, Error: "device-disconnected")];
        using var failing = await world.Route.Chat(Ada, model, Ct);
        var error = await Assert.ThrowsAsync<DeviceUnavailableException>(() => failing!.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, Ct));
        Assert.Equal("device-disconnected", error.Message);

        world.Relay.ChatStatus = HttpStatusCode.Forbidden;
        using var refused = await world.Route.Chat(Ada, model, Ct);
        await Assert.ThrowsAsync<DeviceUnavailableException>(() => refused!.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, Ct));
    }

    [Fact]
    public async Task ARelayThatRefusesTheBrain_OffersNothing()
    {
        await using var world = await World.Open();
        world.Relay.CatalogStatus = HttpStatusCode.Forbidden;

        Assert.Empty(await world.Route.Models(Ada, Ct));
        Assert.Null(await world.Route.Chat(Ada, new DeviceModelRef("pc2", new ModelId("llama3")), Ct));
    }

    [Fact]
    public async Task ARevokedCredential_OffersNothing()
    {
        await using var world = await World.Open();
        world.Relay.CatalogStatus = HttpStatusCode.Unauthorized;

        Assert.Empty(await world.Route.Models(Ada, Ct));
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly TempDirectory _root;
        private readonly HttpClient _http;

        private World(TempDirectory root, FakeTimeProvider clock, FakeRelay relay, HttpClient http, RelayDeviceChatRoute route, string thisMachine, string accessToken)
        {
            _root = root;
            Clock = clock;
            Relay = relay;
            _http = http;
            Route = route;
            ThisMachine = thisMachine;
            AccessToken = accessToken;
        }

        public FakeTimeProvider Clock { get; }
        public FakeRelay Relay { get; }
        public RelayDeviceChatRoute Route { get; }
        public string ThisMachine { get; }
        public string AccessToken { get; }

        public static async Task<World> Open(bool linked = true)
        {
            var root = new TempDirectory("intochat-relay-route-facts-");
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
            var credentials = new DeviceCredentials(new XorProtector(), Path.Combine(root.Path, "devices", "link.bin"));
            const string access = "access-token";
            if (linked)
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                await credentials.Write(new LinkSecrets(new Uri("https://relay.test/"), "cred-1", access, clock.GetUtcNow().AddHours(1),
                    "refresh-token", clock.GetUtcNow().AddDays(30), key.ExportPkcs8PrivateKey()), Ct);
            }
            var relay = new FakeRelay();
            var http = new HttpClient(relay);
            var machine = new MachineId(Path.Combine(root.Path, "machine-id"));
            var route = new RelayDeviceChatRoute(new CloudClient(http, credentials, clock), credentials, machine, clock);
            return new World(root, clock, relay, http, route, machine.Value, access);
        }

        public ValueTask DisposeAsync()
        {
            _http.Dispose();
            _root.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed record Seen(string Method, string Path, string Query, string? Bearer, string Proof, string Body);

    internal sealed class FakeRelay : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public BrainDevicesResponse Devices { get; set; } = new("ada", []);
        public HttpStatusCode CatalogStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode ChatStatus { get; set; } = HttpStatusCode.OK;
        public DeviceChunk[] ChatLines { get; set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues(CircleRoutes.MachineProofHeader, out var proof) ? proof.Single() : "", body));
            if (request.RequestUri.AbsolutePath == DeviceRoutes.Catalog)
            {
                return CatalogStatus != HttpStatusCode.OK
                    ? new HttpResponseMessage(CatalogStatus)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Devices, DeviceWireJson.Default.BrainDevicesResponse), Encoding.UTF8, "application/json") };
            }
            if (request.RequestUri.AbsolutePath == DeviceRoutes.Chat)
            {
                if (ChatStatus != HttpStatusCode.OK) { return new HttpResponseMessage(ChatStatus); }
                var lines = string.Join('\n', ChatLines.Select(chunk => JsonSerializer.Serialize(chunk, DeviceWireJson.Default.DeviceChunk)));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lines + "\n", Encoding.UTF8, "application/x-ndjson") };
            }
            // The relay refuses a revoked credential's refresh as well.
            return new HttpResponseMessage(request.RequestUri.AbsolutePath == CircleRoutes.Refresh ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound);
        }
    }

    private sealed class XorProtector : IMachineKeyProtector
    {
        private const byte Mask = 0x5A;
        public byte[] Protect(byte[] key) => [.. key.Select(b => (byte)(b ^ Mask))];
        public byte[] Unprotect(byte[] stored) => [.. stored.Select(b => (byte)(b ^ Mask))];
    }
}
