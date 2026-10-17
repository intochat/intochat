namespace DigitalBrain.Monnet.Signals;

[GenerateSerializer, Alias("monnet.payment-order-created")]
public sealed record PaymentOrderCreated([property: Id(0)] PaymentOrder Order) : Signal;

[GenerateSerializer, Alias("monnet.payment-received")]
public sealed record PaymentReceived([property: Id(0)] PaymentReceipt Receipt) : Signal;

// Detail is the provider's status, e.g. "unauthorized (401)".
[GenerateSerializer, Alias("monnet.payment-failed")]
public sealed record PaymentFailed([property: Id(0)] string Reference, [property: Id(1)] string Detail) : Signal;
