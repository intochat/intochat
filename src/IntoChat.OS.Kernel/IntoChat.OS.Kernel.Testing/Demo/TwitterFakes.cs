using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orleans;

namespace IntoChat.OS.Kernel.Testing.Demo;

[Alias("twitter")]
[Orleans.Metadata.DefaultGrainType("twitter")]
public interface ITwitterAccount : INeuron
{
    Task Post(string text);
}

[Alias("bitcoin")]
[Orleans.Metadata.DefaultGrainType("bitcoin")]
public interface IBitcoin : INeuron
{
    Task SetPrice(decimal usd);
    Task<decimal> GetPrice();
}

[GenerateSerializer, Alias("test.posted")]
public sealed record Posted([property: Id(0)] string From, [property: Id(1)] string Text) : Signal;

public sealed record TweetWebhook(string Account, string Text);

public sealed class TestTwitterModule() : Module(new ModuleId("twitter")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(ITwitterAccount).Assembly;
    public static IntoChat.OS.Connections.Platform.Integrations.ConnectorDefinition Connector => new("twitter", "Twitter fixture", ["api-key"], []);
    public override void Configure(ISiloBuilder builder) { }

    public void Configure(IEndpointRouteBuilder endpoints)
        => endpoints.MapPost("/twitter/webhook", async (TweetWebhook tweet, IIntoChatOSBrains brains) =>
        {
            await brains.Grain<ITwitterAccount>(tweet.Account).Post(tweet.Text);
            return Results.Accepted();
        });
}

[GrainType("twitter")]
public sealed class TwitterAccount : Neuron, ITwitterAccount
{
    public Task Post(string text) => PublishAsync(new Posted(this.GetPrimaryKeyString(), text));
}
[GrainType("bitcoin")]
public sealed class Bitcoin : Neuron, IBitcoin
{
    private decimal _usd;
    public Task SetPrice(decimal usd) { _usd = usd; return Task.CompletedTask; }
    public Task<decimal> GetPrice() => Task.FromResult(_usd);
}
