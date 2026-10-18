using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace IntoChat.OS.Framework.Compute;

// Meters every call a factory client makes: the item is "{clientName}:{method}" so the ledger can
// group a connector's traffic. A request that carries the integration-funding header bills through
// the deployment integration; everything else rides the payer's own connection.
public sealed class MeteredHttpHandler(ICostMeter meter, string clientName) : DelegatingHandler
{
    public const string IntegrationFundingHeader = "x-intochatos-funding";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var integration = request.Headers.TryGetValues(IntegrationFundingHeader, out var values)
            && values.Any(value => string.Equals(value, "integration", StringComparison.OrdinalIgnoreCase));
        try
        {
            await meter.RecordAsync(CostKind.Http, $"{clientName}:{request.Method.Method}", 1m, "call",
                integration ? Funding.Integration : Funding.OwnConnection, Guid.NewGuid().ToString("N"), cancellationToken);
        }
        catch (Exception exception) when (exception is not BudgetExhaustedException)
        {
            // Metering observes the call; a call with no trace or caller (token refresh, startup) still goes through.
        }
        return response;
    }
}

// The factory knows the client name only at handler-build time, on the builder. .NET's own logging
// and metrics handlers use a builder filter for exactly this reason; a handler resolved from DI does
// not get the name. Without a registered meter the filter adds nothing, so raw HttpClient users stay
// unmetered.
public sealed class MeteredHttpHandlerFilter(IServiceProvider services) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        if (services.GetService<ICostMeter>() is { } meter)
        {
            builder.AdditionalHandlers.Add(new MeteredHttpHandler(meter, builder.Name ?? ""));
        }
        next(builder);
    };
}
