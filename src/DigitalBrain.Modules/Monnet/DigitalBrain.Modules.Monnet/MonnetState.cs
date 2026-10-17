using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Monnet;

// A connection's Account is its merchant id.
[GenerateSerializer, Alias("monnet.state")]
internal sealed class MonnetState : ConnectionState
{
    [Id(8)] public PaymentOrder[] Pending { get; set; } = [];
    [Id(9)] public PaymentReceipt[] Received { get; set; } = [];
    // Provider payment ids whose final outcome the webhook already delivered (at-least-once delivery).
    [Id(10)] public string[] Seen { get; set; } = [];
    [Id(11)] public string? LastFailure { get; set; }
}

// ASSUMED webhook body until Monnet documents it (README, "Assumed API").
[GenerateSerializer, Alias("monnet.webhook-payload")]
internal sealed record MonnetWebhookPayload(
    [property: Id(0)] string MerchantId,
    [property: Id(1)] string PaymentId,
    [property: Id(2)] string Reference,
    [property: Id(3)] decimal Amount,
    [property: Id(4)] string Currency,
    [property: Id(5)] string Status);

[Alias("monnet.inbound"), Orleans.Metadata.DefaultGrainType("monnet")]
internal interface IMonnetInbound : IGrainWithStringKey
{
    Task AcceptWebhook(MonnetWebhookPayload payload);
}
