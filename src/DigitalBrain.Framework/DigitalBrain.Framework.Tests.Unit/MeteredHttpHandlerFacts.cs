using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class MeteredHttpHandlerFacts
{
    private const string Client = "gmail";

    [Fact(Timeout = 60_000)]
    public async Task AnHttpCallThroughAFactoryClientIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        await using var provider = Provider(meter);

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(Client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/send");

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var recorded = Assert.Single(meter.Calls);
        Assert.Equal(CostKind.Http, recorded.Kind);
        Assert.Equal($"{Client}:POST", recorded.Item);
        Assert.Equal(1m, recorded.Quantity);
        Assert.Equal("call", recorded.Unit);
        Assert.Equal(Funding.OwnConnection, recorded.Funding);
        Assert.False(string.IsNullOrWhiteSpace(recorded.CostId));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnIntegrationFundedCallIsBilledToTheDeployment()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        await using var provider = Provider(meter);

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(Client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/send");
        request.Headers.Add(MeteredHttpHandler.IntegrationFundingHeader, "integration");

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(Funding.Integration, Assert.Single(meter.Calls).Funding);
    }

    private static ServiceProvider Provider(RecordingMeter meter)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICostMeter>(meter);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, MeteredHttpHandlerFilter>();
        services.AddHttpClient(Client).ConfigurePrimaryHttpMessageHandler(static () => new StubHandler());
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed record Recorded(CostKind Kind, string Item, decimal Quantity, string Unit, Funding Funding, string CostId);

    private sealed class RecordingMeter : ICostMeter
    {
        public ConcurrentQueue<Recorded> Calls { get; } = new();

        public ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken, CostSubject? subject = null)
        {
            Calls.Enqueue(new Recorded(kind, item, quantity, unit, funding, costId));
            return ValueTask.FromResult(new Cost
            {
                TraceId = "trace",
                CostId = costId,
                Kind = kind,
                Item = item,
                Quantity = quantity,
                Unit = unit,
                Micros = 0,
                At = DateTimeOffset.UtcNow,
            });
        }
    }
}
