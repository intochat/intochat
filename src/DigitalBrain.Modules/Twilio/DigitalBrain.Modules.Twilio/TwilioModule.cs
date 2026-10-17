using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Twilio.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Twilio;

// A brain connects an account either by pasting it ("credentials") or by using the deployment's own
// ("deployment"). Twilio calls back on StatusPath/{brainId} with each delivery status.
public sealed class TwilioModule() : Module<TwilioModuleOptions>(new ModuleId("twilio")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(ITwilioSms).Assembly;

    public const string HttpClientName = "DigitalBrain.Twilio";
    public const string StatusPath = "/twilio/status";

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("twilio", "Twilio")
        .RequiresSetting("AccountSid").RequiresSecret("AuthToken").RequiresSetting("FromNumber");

    // "credentials": the pasted value is AccountSid=…;AuthToken=…;From=…, read like a connection string.
    // "deployment": the value is ignored; the connection sends on the deployment's Twilio integration.
    public static ConnectorDefinition Connector { get; } = new("twilio", "Twilio SMS", ["credentials", "deployment"], ["twilio"])
    {
        Neurons = [typeof(ITwilioSms)],
        Signals = [typeof(SmsSent), typeof(SmsDelivered), typeof(SmsFailed)],
    };

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        var options = silo.Configuration.GetModuleOptions<TwilioModuleOptions>("twilio");
        // Resolved at composition, so a host with a misconfigured base URL fails to start.
        options.Validate();
        services.TryAddSingleton(options);
        var origin = Uri.TryCreate(silo.Configuration[DigitalBrainConfiguration.ConnectionsPublicOriginKey], UriKind.Absolute, out var parsed) ? parsed : null;
        services.TryAddSingleton(new TwilioCallbackOrigin(origin));
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton<ITwilioProvider, HttpTwilioProvider>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(StatusPath + "/{brainId}", async (string brainId, HttpRequest request, TwilioCallbackOrigin origin, IGrainFactory grains, CancellationToken ct) =>
        {
            if (!BrainScope.IsValidId(brainId)) { return Results.NotFound(); }
            if (!request.HasFormContentType) { return Results.BadRequest(); }
            var form = await request.ReadFormAsync(ct);
            var signature = request.Headers[TwilioSignature.Header].ToString();
            if (string.IsNullOrWhiteSpace(form["AccountSid"].ToString()) || string.IsNullOrWhiteSpace(form["MessageSid"].ToString())) { return Results.BadRequest(); }
            if (string.IsNullOrEmpty(signature)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }

            // Twilio signs the URL it called; behind a proxy that is the public origin, not the one this host sees.
            var url = origin.StatusCallback(brainId) is { } callback ? callback.AbsoluteUri + request.QueryString : request.GetDisplayUrl();
            TwilioFormField[] fields = [.. form.SelectMany(pair => pair.Value.Select(value => new TwilioFormField(pair.Key, value ?? "")))];
            var delivery = new TwilioStatusCallback(url, fields, signature);

            var accepted = false;
            foreach (var key in await grains.GetGrain<IConnectionIndex>(brainId).List())
            {
                if (ConnectionKeys.Parse(key).ConnectorId != Connector.Id) { continue; }
                accepted |= await grains.GetGrain<ITwilioInbound>(key).AcceptStatus(delivery);
            }
            return accepted ? Results.NoContent() : Results.StatusCode(StatusCodes.Status403Forbidden);
        }).AllowAnonymous().DisableAntiforgery();
    }
}
