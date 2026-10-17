using System.Globalization;
using System.Text.Json.Nodes;
using DigitalBrain.Salesforce;

// Scope S8/S9: each payment becomes a Loan_Transaction__c with the provider reference, the date, the
// amount and the channel. The field API names and the createRecord argument shape are assumptions
// until the org's schema is read (ISalesforce.ReadSchema).
static class SalesforceRecords
{
    public const string LoanTransaction = "Loan_Transaction__c";

    // The Monnet statuses that mean the money arrived (the same set the Monnet webhook publishes PaymentReceived for).
    public static bool IsPaid(string? status) =>
        status?.Trim().ToLowerInvariant() is "paid" or "succeeded" or "completed" or "approved";

    public static string LoanTransactionArguments(string paymentId, string reference, decimal amount, string currency, DateTimeOffset at) =>
        new JsonObject
        {
            ["objectApiName"] = LoanTransaction,
            ["fields"] = new JsonObject
            {
                ["Provider_Reference__c"] = paymentId,
                ["Loan_Reference__c"] = reference,
                ["Amount__c"] = amount,
                ["Currency__c"] = currency,
                ["Transaction_Date__c"] = at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["Channel__c"] = "monnet",
                ["Type__c"] = "Payment",
            },
        }.ToJsonString();

    // The module's two-step write: prepare a preview, then confirm it; the confirmed preview carries the record id.
    public static async Task<SalesforceWritePreview> WriteLoanTransaction(ISalesforce salesforce,
        string paymentId, string reference, decimal amount, string currency, DateTimeOffset at)
    {
        var prepared = await salesforce.PrepareWrite(new("createRecord", LoanTransactionArguments(paymentId, reference, amount, currency, at)));
        return await salesforce.ConfirmWrite(new(prepared.PreviewId, prepared.ToolSchemaHash));
    }
}
