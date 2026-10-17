using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Monnet;
using DigitalBrain.Salesforce;

// One reaction: a Monnet payment id is read and written to Salesforce as a Loan Transaction, then marked
// recorded so the webhook reaction does not write it again.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
await foreach (var request in brain.On(app, "record-payment", brain.Stopping))
{
    var paymentId = request.Input.Trim();
    await Spike.Run(brain, key, "record-payment", request.RequestId, paymentId, async () =>
    {
        var monnet = brain.Get<IMonnet>(Spike.Require(brain, "monnet", "monnet"));
        var salesforce = brain.Get<ISalesforce>(Spike.Require(brain, "salesforce", "salesforce"));
        if (paymentId.Length == 0) { throw new InvalidOperationException("Give a Monnet payment id."); }
        var marker = brain.Get<IText>($"{key}/recorded/{paymentId}");
        var recorded = (await marker.Read()).Markdown;
        if (recorded.Length > 0) { throw new InvalidOperationException($"Payment {paymentId} is already recorded as {recorded}."); }
        var payment = await monnet.ReadPayment(new(paymentId));
        // Only money Monnet confirms as received becomes a transaction; the webhook path applies the same rule.
        if (!SalesforceRecords.IsPaid(payment.Status)) { throw new InvalidOperationException($"Payment {paymentId} is {payment.Status}, not paid; nothing recorded."); }
        var written = await SalesforceRecords.WriteLoanTransaction(salesforce, payment.PaymentId, payment.Reference,
            payment.Amount, payment.Currency, DateTimeOffset.UtcNow);
        await marker.Set(written.RecordId ?? written.PreviewId);
        return ($"payment {payment.PaymentId} ({payment.Status}) {payment.Amount} {payment.Currency} -> {SalesforceRecords.LoanTransaction} {written.RecordId}", payment.Status);
    });
}
