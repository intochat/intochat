namespace DigitalBrain.Monnet;

internal interface IMonnetProvider
{
    Task<PaymentOrder> CreatePaymentOrderAsync(MonnetAccount account, CreatePaymentOrder command, CancellationToken ct);
    Task<PaymentStatus> ReadPaymentAsync(MonnetAccount account, string paymentId, CancellationToken ct);
    // Null when the merchant answers; otherwise the failure detail, e.g. "unauthorized (401)".
    Task<string?> ProbeAsync(MonnetAccount account, CancellationToken ct);
}
