using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Monnet.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Monnet;

// A brain connects a merchant by pasting its credentials or by choosing the deployment's own
// (auth method `deployment`). Monnet's API is not published yet: see README for the assumed shape.
public sealed class MonnetModule() : Module<MonnetModuleOptions>(new ModuleId("monnet")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(IMonnet).Assembly;

    public const string HttpClientName = "DigitalBrain.Monnet";
    public const string WebhookPath = "/monnet/webhook";
    public const string SignatureHeader = "X-Monnet-Signature";
    private const int MaxWebhookBytes = 64 * 1024;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("monnet", "Monnet")
        .RequiresSecret("ApiKey").RequiresSetting("MerchantId").OffersSecret("WebhookSecret").OffersSetting("BaseUrl");

    public static ConnectorDefinition Connector { get; } = new("monnet", "Monnet", ["credentials", MonnetNeuron.DeploymentMethod], ["monnet"])
    {
        Neurons = [typeof(IMonnet)],
        Signals = [typeof(PaymentOrderCreated), typeof(PaymentReceived), typeof(PaymentFailed)],
    };

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        var options = silo.Configuration.GetModuleOptions<MonnetModuleOptions>("monnet");
        // Resolved at composition, so a host with a misconfigured base URL fails to start.
        options.Validate();
        services.TryAddSingleton(options);
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton<IMonnetProvider, HttpMonnetProvider>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(WebhookPath + "/{brainId}", async (string brainId, HttpRequest request, IModuleIntegrations<MonnetModule> integrations, IGrainFactory grains, CancellationToken ct) =>
        {
            if (!BrainScope.IsValidId(brainId)) { return Results.NotFound(); }
            var body = await ReadBodyAsync(request, ct);
            if (body is null) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            var secret = await ReadWebhookSecretAsync(integrations, ct);
            if (secret is null || !SignatureMatches(secret, body, request.Headers[SignatureHeader].ToString())) { return Results.Unauthorized(); }

            MonnetWebhookPayload? payload;
            try { payload = JsonSerializer.Deserialize<MonnetWebhookPayload>(body, MonnetJson.Options); }
            catch (JsonException) { return Results.BadRequest(); }
            if (payload is null || string.IsNullOrWhiteSpace(payload.MerchantId) || string.IsNullOrWhiteSpace(payload.PaymentId))
            { return Results.BadRequest(); }

            // Each Monnet connection of the brain decides by its own merchant id whether the delivery is its.
            foreach (var key in await grains.GetGrain<IConnectionIndex>(brainId).List())
            {
                if (ConnectionKeys.Parse(key).ConnectorId == "monnet") { await grains.GetGrain<IMonnetInbound>(key).AcceptWebhook(payload); }
            }
            return Results.Accepted();
        }).AllowAnonymous();
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxWebhookBytes) { return null; }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Without a WebhookSecret no delivery can be authenticated, so every one is refused.
    private static async Task<string?> ReadWebhookSecretAsync(IModuleIntegrations<MonnetModule> integrations, CancellationToken ct)
    {
        try
        {
            var secret = await integrations.Get("monnet").ReadSecretAsync("WebhookSecret", ct);
            return string.IsNullOrEmpty(secret) ? null : secret;
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or ArgumentException) { return null; }
    }

    internal static string Sign(string secret, ReadOnlySpan<byte> body)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    internal static bool SignatureMatches(string secret, byte[] body, string header)
    {
        var presented = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? header[7..] : header;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, body));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(presented.Trim().ToLowerInvariant()));
    }
}
