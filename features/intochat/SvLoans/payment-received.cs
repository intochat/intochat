using DigitalBrain.Flutter.Text;
using DigitalBrain.Monnet;
using DigitalBrain.Monnet.Signals;
using DigitalBrain.Salesforce;

// One reaction (decision D4's buffer service): a payment Monnet's webhook accepted is written to Salesforce
// once. Delivery is at-least-once, so {app}/recorded/{paymentId} is the dedup marker.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var monnetKey = Spike.Neuron(brain, "monnet", "monnet");
if (monnetKey is null)
{
    // Nothing to subscribe to until a Monnet connection is bound; binding re-runs this script.
    var status = brain.Get<IText>($"{key}/status");
    var unbound = Spike.Unbound("monnet", "monnet");
    if ((await status.Read()).Markdown != unbound) { await status.Set(unbound); }
    try { await Task.Delay(Timeout.Infinite, brain.Stopping); } catch (OperationCanceledException) { }
    return;
}
var monnet = brain.Get<IMonnet>(monnetKey);
await foreach (var received in brain.On<PaymentReceived>(monnet, brain.Stopping))
{
    var receipt = received.Receipt;
    var marker = brain.Get<IText>($"{key}/recorded/{receipt.PaymentId}");
    if ((await marker.Read()).Markdown.Length > 0) { continue; }
    var requestId = Guid.NewGuid();
    await Spike.Run(brain, key, "record-payment", requestId, receipt.PaymentId, async () =>
    {
        var salesforce = brain.Get<ISalesforce>(Spike.Require(brain, "salesforce", "salesforce"));
        var written = await SalesforceRecords.WriteLoanTransaction(salesforce, receipt.PaymentId, receipt.Reference,
            receipt.Amount, receipt.Currency, receipt.ReceivedAt);
        await marker.Set(written.RecordId ?? written.PreviewId);
        return ($"webhook payment {receipt.PaymentId} {receipt.Amount} {receipt.Currency} -> {SalesforceRecords.LoanTransaction} {written.RecordId}", receipt.Status);
    });
}
