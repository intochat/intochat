using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Monnet;

// One reaction: an amount and a loan reference open a Monnet payment order in PEN.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
await foreach (var request in brain.On(app, "create-payment", brain.Stopping))
{
    await Spike.Run(brain, key, "create-payment", request.RequestId, request.Input, async () =>
    {
        var monnet = brain.Get<IMonnet>(Spike.Require(brain, "monnet", "monnet"));
        var input = Spike.ParseObject(request.Input);
        if (!input.TryGetProperty("amount", out var amountValue) || amountValue.ValueKind != JsonValueKind.Number
            || !amountValue.TryGetDecimal(out var amount) || amount <= 0)
        { throw new InvalidOperationException("Give a positive numeric amount."); }
        var reference = Spike.Text(input, "reference");
        if (string.IsNullOrWhiteSpace(reference)) { throw new InvalidOperationException("Give a payment reference."); }
        var order = await monnet.CreatePaymentOrder(new(amount, reference, $"SV loan {reference}", "PEN", Spike.Text(input, "phone")));
        return ($"paymentId={order.PaymentId} amount={order.Amount} {order.Currency} checkout={order.CheckoutUrl}", order.Status);
    });
}
