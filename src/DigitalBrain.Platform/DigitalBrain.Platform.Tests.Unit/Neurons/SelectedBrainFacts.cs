using System.Net.Http.Json;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Neurons.Proto;
using DigitalBrain.Testing.Neurons;
using Google.Protobuf;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Neurons;

public sealed class SelectedBrainFacts
{
    [Fact]
    public async Task CallsAndWatchesUseTheAuthorizedSelectedBrainRatherThanTheLoginBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        using var person = await brain.PersonAsync("selected-brain-member", ct);
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        var provision = await identity.CreateBrain(person.AccountId, person.PrincipalId, "second", "Second", BrainKind.Personal);
        var destination = provision.Member.BrainId;
        using (person.AsCallerIn(destination))
        {
            await brain.Get<IBrain>(destination).Create(new Owner(person.AccountId));
        }
        using var handler = new HttpClientHandler();
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = brain.HttpClient.BaseAddress };
        using var login = await http.PostAsJsonAsync("/identity/login", new { principalId = person.PrincipalId, password = person.PrincipalId + "-password-123" }, ct);
        login.EnsureSuccessStatusCode();
        using var channel = GrpcChannel.ForAddress(http.BaseAddress!, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, handler),
            HttpVersion = new Version(1, 1), HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        });
        var client = new DigitalBrain.Platform.Neurons.Proto.Neurons.NeuronsClient(channel);
        var key = destination + "/selected-pulse";
        await client.CallAsync(new CallRequest
        {
            BrainId = destination, Contract = typeof(IPulse).FullName, Key = key,
            Method = nameof(IPulse.Pulse), Arguments = ByteString.CopyFromUtf8("[7]"),
        }, cancellationToken: ct);
        var watch = new WatchRequest { BrainId = destination };
        watch.Sources.Add(new WatchSource { Contract = typeof(IPulse).FullName, Key = key, SignalTypes = { nameof(Pulsed) } });
        using var stream = client.Watch(watch, cancellationToken: ct);
        Assert.True(await stream.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(key, stream.ResponseStream.Current.Source);
    }
}
