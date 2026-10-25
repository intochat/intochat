using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Framework.Diagnostics;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Framework.Modules.Metering;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Connections.Compute;

// Meters every call a factory client makes: the item is "{clientName}:{method}" so the ledger can
// group a connector's traffic. Funding comes from explicit module metadata on enrolled clients;
// this fallback records personal connection traffic without inferring authority from HTTP headers.
public sealed class MeteredHttpHandler(ICostMeter meter, string clientName, ILogger<MeteredHttpHandler>? logger = null) : DelegatingHandler
{
    public const string IntegrationFundingHeader = "x-intochatos-funding";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(IntegrationFundingHeader);
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            await meter.RecordAsync(CostKind.Http, $"{clientName}:{request.Method.Method}", 1m, "call",
                Funding.OwnConnection, Guid.NewGuid().ToString("N"), cancellationToken);
        }
        catch (Exception exception) when (exception is not BudgetExhaustedException)
        {
            // Metering observes the call; a call with no trace or caller (token refresh, startup) still goes through.
            logger?.LogSwallowed(exception, "http call metering failed");
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
        next(builder);
        if (!services.GetServices<MeteredHttpClientRegistration>().Any(registration => registration.Name == builder.Name)
            && services.GetService<ICostMeter>() is { } meter)
        {
            builder.AdditionalHandlers.Add(new MeteredHttpHandler(meter, builder.Name ?? "", services.GetService<ILogger<MeteredHttpHandler>>()));
        }
    };
}
