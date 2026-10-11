using Orleans.Concurrency;

namespace DigitalBrain.Monnet;

// One merchant at Monnet, served on a connection key {brain}/connection/monnet/{id}: the merchant a member
// of the brain pasted (`credentials`) or the deployment's own (`deployment`).
[Alias("monnet"), Orleans.Metadata.DefaultGrainType("monnet")]
public interface IMonnet : INeuron
{
    Task<PaymentOrder> CreatePaymentOrder(CreatePaymentOrder command);

    Task<PaymentStatus> ReadPayment(ReadPayment query);

    // Payments the webhook accepted, newest first.
    [ReadOnly]
    Task<PaymentReceipt[]> ReadReceived(int limit);
}

[GenerateSerializer, Alias("monnet.create-payment-order")]
public sealed record CreatePaymentOrder(
    [property: Id(0)] decimal Amount,
    [property: Id(1)] string Reference,
    [property: Id(2)] string Description,
    [property: Id(3)] string Currency = "PEN",
    [property: Id(4)] string? PayerPhone = null,
    [property: Id(5)] string? PayerDni = null);

[GenerateSerializer, Alias("monnet.read-payment")]
public sealed record ReadPayment([property: Id(0)] string PaymentId);

[GenerateSerializer, Alias("monnet.payment-order")]
public sealed record PaymentOrder(
    [property: Id(0)] string PaymentId,
    [property: Id(1)] string Reference,
    [property: Id(2)] decimal Amount,
    [property: Id(3)] string Currency,
    [property: Id(4)] string Status,
    [property: Id(5)] string? CheckoutUrl,
    [property: Id(6)] DateTimeOffset CreatedAt);

[GenerateSerializer, Alias("monnet.payment-status")]
public sealed record PaymentStatus(
    [property: Id(0)] string PaymentId,
    [property: Id(1)] string Reference,
    [property: Id(2)] string Status,
    [property: Id(3)] decimal Amount,
    [property: Id(4)] string Currency);

[GenerateSerializer, Alias("monnet.payment-receipt")]
public sealed record PaymentReceipt(
    [property: Id(0)] string PaymentId,
    [property: Id(1)] string Reference,
    [property: Id(2)] decimal Amount,
    [property: Id(3)] string Currency,
    [property: Id(4)] string Status,
    [property: Id(5)] DateTimeOffset ReceivedAt);

// Messages are the module's own fixed sentences with the provider's status; never a key or a URL.
[GenerateSerializer, Alias("monnet.unavailable")]
public sealed class MonnetUnavailableException(string message) : InvalidOperationException(message);

[GenerateSerializer, Alias("monnet.not-connected")]
public sealed class MonnetNotConnectedException(string message = "Monnet is not connected. Connect a Monnet merchant or configure the deployment's integration.") : InvalidOperationException(message);
