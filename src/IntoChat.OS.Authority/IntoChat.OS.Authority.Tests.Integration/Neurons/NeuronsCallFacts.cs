using System.Text.Json;
using IntoChat.OS.Authority.Neurons.Proto;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;

namespace IntoChat.OS.Authority.Tests.Integration.Neurons;

public sealed class NeuronsCallFacts(AuthorityHost host)
{
    // gRPC-Web over the edge's HTTP/1.1 Kestrel, exactly what the web shell does.
    internal static IntoChat.OS.Authority.Neurons.Proto.Neurons.NeuronsClient Client(ITestBrain brain)
        => new(GrpcChannel.ForAddress(brain.HttpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler()),
            HttpVersion = new Version(1, 1),
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        }));

    private static CallRequest Read(string brainId, string arguments = "[]") => new()
    {
        BrainId = brainId,
        Contract = typeof(IBrain).FullName,
        Key = "owner",
        Method = nameof(IBrain.Read),
        Arguments = ByteString.CopyFromUtf8(arguments),
    };

    [Fact]
    public async Task ACallRunsAContractMethodAndAnswersItsResultAsJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var reply = await Client(brain).CallAsync(Read("owner"), cancellationToken: ct);
        using var json = JsonDocument.Parse(reply.Result.ToStringUtf8());
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task BadArgumentsAreAnInvalidArgument()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var error = await Assert.ThrowsAsync<RpcException>(() => Client(brain).CallAsync(Read("owner", "not json"), cancellationToken: ct).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("JSON array", error.Status.Detail);
    }

    [Fact]
    public async Task AnInvalidBrainIdIsAnInvalidArgument()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var error = await Assert.ThrowsAsync<RpcException>(() => Client(brain).CallAsync(Read("a/b"), cancellationToken: ct).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }
}
