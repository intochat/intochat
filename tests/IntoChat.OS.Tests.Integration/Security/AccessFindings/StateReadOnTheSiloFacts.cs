using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Kernel;
using Google.Protobuf;
using IntoChat.OS.Kernel.Platform.Proto;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// A neuron's inspectable state, read on the real silo through the real gRPC service (NeuronsGrpcService.ReadState over
// gRPC-Web, the framing the Flutter web client sends), then through the kernel's observer directly. The read returns
// state only to a caller the brain admits.
[Trait("Category", "AccessFindings")]
public sealed class StateReadOnTheSiloFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Ok = 0;
    private const int PermissionDenied = 7;
    private const int Unauthenticated = 16;

    [Fact]
    public async Task TheOwner_ReadsTheStateOfANeuronOfTheirBrain_OverGrpc()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        await MintRoster(host);

        var (status, reply) = await ReadState(host.Client, host.Scope, presence: host.PresenceToken);

        Assert.Equal(Ok, status);
        // The roster's own record: the pending code Mint just stored.
        Assert.Contains("\"pending\":[{", reply!.StateJson.ToStringUtf8(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallWithNoSession_ReadsNoState_OverGrpc()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        await MintRoster(host);

        var (status, reply) = await ReadState(host.Client, host.Scope, presence: null);

        Assert.Equal(Unauthenticated, status);
        Assert.Null(reply);
    }

    [Fact]
    public async Task ASignedInPersonOfAnotherBrain_ReadsNoState_OverGrpc()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        await MintRoster(host);
        using var stranger = await SignedIn(host, "mallory-" + Guid.NewGuid().ToString("N")[..8]);

        var (status, reply) = await ReadState(stranger, host.Scope, presence: null);

        Assert.Equal(PermissionDenied, status);
        Assert.Null(reply);
    }

    [Fact]
    public async Task TheKernelObserver_ReturnsState_OnlyUnderACallerOfTheBrain()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        await MintRoster(host);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        var observer = host.Services.GetRequiredService<IBrainObserver>();
        var roster = FriendsDefaults.Id(host.Scope);
        Orleans.Runtime.RequestContext.Clear();

        // No caller: refused by the silo's default rule.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => observer.State(roster, Ct));

        // A person of another brain: refused by the cross-brain wall.
        var other = new BrainId("mallory" + Guid.NewGuid().ToString("N")[..8]);
        using (edge.Enter(edge.Person(other.Value, other.Value, other)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => observer.State(roster, Ct));
        }

        // The brain's own person, as the gRPC edge stamps one after its membership decision.
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(host.Scope))))
        {
            Assert.NotNull(await observer.State(roster, Ct));
        }
    }

    private static async Task MintRoster(Gate20EdgeApprovalWallFacts.EdgeKernelHarness host)
    {
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(host.Scope))))
        {
            await DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IFriends>(host.Services, FriendsDefaults.Id(host.Scope)).Mint();
        }
    }

    // A second person with their own brain and session cookie on this host.
    private static async Task<HttpClient> SignedIn(Gate20EdgeApprovalWallFacts.EdgeKernelHarness host, string principal)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = host.BaseAddress };
        using var registered = await client.PostAsJsonAsync(
            "/identity/register", new { principalId = principal, password = "correct-horse-battery-staple", displayName = principal }, Ct);
        registered.EnsureSuccessStatusCode();
        return client;
    }

    // One unary gRPC-Web exchange: a length-prefixed message out; data frames and a trailers frame (or trailers-only headers) back.
    private static async Task<(int Status, NeuronStateReply? Reply)> ReadState(HttpClient client, string brain, string? presence)
    {
        var message = new NeuronStateRequest { BrainId = brain, Contract = typeof(IFriends).FullName, Key = $"{brain}/{FriendsDefaults.KeyPart}" }.ToByteArray();
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
        message.CopyTo(frame, 5);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/intochat.os.Neurons/ReadState") { Content = new ByteArrayContent(frame) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
        request.Headers.Add("X-Grpc-Web", "1");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        if (presence is not null) { request.Headers.Add(PresenceProof.HeaderName, presence); }

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);
        int? status = response.Headers.TryGetValues("grpc-status", out var early) ? int.Parse(early.First(), System.Globalization.CultureInfo.InvariantCulture) : null;
        NeuronStateReply? reply = null;
        for (var at = 0; at + 5 <= body.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(at + 1));
            var payload = body.AsSpan(at + 5, length);
            if ((body[at] & 0x80) == 0) { reply = NeuronStateReply.Parser.ParseFrom(payload); }
            else
            {
                foreach (var line in Encoding.ASCII.GetString(payload).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("grpc-status:", StringComparison.OrdinalIgnoreCase))
                    { status = int.Parse(line["grpc-status:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture); }
                }
            }
            at += 5 + length;
        }

        // The session middleware answers before the gRPC service does when the request carries no session at all.
        status ??= response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => Unauthenticated,
            System.Net.HttpStatusCode.Forbidden => PermissionDenied,
            _ => null,
        };
        Assert.True(status is not null, $"No gRPC status came back (HTTP {(int)response.StatusCode}).");
        return (status!.Value, status == Ok ? reply : null);
    }
}
