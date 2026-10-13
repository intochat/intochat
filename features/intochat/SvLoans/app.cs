/*
# SV Loans

The SV integration spikes as one app: the credit bureau (Equifax), fraud scoring (RiskSeal), SMS
one-time codes (Twilio), payment collection (Monnet) and the loan book (Salesforce). Each operation
calls one provider and records what happened as neuron state; nothing waits for a script.

Every operation writes its result to {app}/results/{operation}/{requestId} and to
{app}/results/{operation}/latest: JSON with the operation, the input (OTP codes and secrets
redacted), the outcome "ok" or "failed", the detail and the provider's status. A provider failure
is a "failed" result carrying the provider's message; it never stops the app.

Each provider is a slot: salesforce, monnet, equifax, riskseal and sms (a Twilio account). A bound
slot uses that connection, whether the member's own account or the deployment's (a connection made
with auth method "deployment"); there is no fallback.

Payments Monnet confirms through its webhook are written to Salesforce as a Loan_Transaction__c
with the provider reference, the amount, the date and channel "monnet", exactly once per payment.

## Scenario: Checking the bureau reads the credit report

Asking "check-bureau" with "12345678" reads that DNI's Equifax report; the result holds the
report's flags. Input that is not exactly eight digits fails without calling Equifax.

## Scenario: Scoring risk asks RiskSeal

Asking "score-risk" with {"email":"a@b.pe","phone":"+51987654321","ip":"1.2.3.4","dni":"12345678"}
scores the applicant; the result holds the score and the risk level. Input without an email or a
phone fails without calling RiskSeal.

## Scenario: Sending an OTP texts a six-digit code

Asking "send-otp" with "+51987654321" generates a six-digit code, keeps it in
{app}/otp/+51987654321 and texts "Tu código SV es {code}" to that phone. The result never holds
the code. A repeated request sends once.

## Scenario: Creating a payment opens a Monnet order

Asking "create-payment" with {"amount":150.50,"reference":"LOAN-1","phone":"+51987654321"} creates
a payment order in PEN; the result holds the payment id and the checkout URL.

## Scenario: Recording a payment writes a Loan Transaction

Asking "record-payment" with a Monnet payment id reads the payment and writes a
Loan_Transaction__c to Salesforce; the result holds the Salesforce record id. Only a payment Monnet
reports as paid is written, and a payment already marked in {app}/recorded/{paymentId} (by this
operation or by the webhook reaction) is refused instead of written twice.

## Scenario: A received payment reaches Salesforce once

When Monnet's webhook reports a payment, the app writes it to Salesforce as a Loan_Transaction__c
and marks {app}/recorded/{paymentId}; the same payment delivered again is not written twice.

## Scenario: A provider failure is a failed result

With fake credentials, "check-bureau" with "12345678" records outcome "failed" with the provider's
status (for example "unauthorized (401)"), and the next request still runs.

## Scenario: An unbound slot asks for a binding

With no connection bound to the equifax slot, "check-bureau" with "12345678" records outcome "failed"
with detail "Bind a equifax connection to this app's equifax slot in app configuration, then retry."
without calling any provider, and the next request still runs. With the monnet slot unbound, the
webhook reaction writes that detail for monnet to {app}/status once and waits.

## Scenario: Status summarizes the last results

Asking "status" writes {app}/status: one line per operation with its last outcome and detail, or
"no result yet".

Startup publishes one sphere and a main layout hosting the existing status content.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "sv-loans";
    public const string Version = "0.1.3";

    public static readonly AppManifest Manifest = new(
        Title: "SV Loans",
        Description: "SV loan integration spikes: Equifax bureau, RiskSeal scoring, Twilio OTP, Monnet payments and Salesforce loan transactions.",
        Operations:
        [
            new("check-bureau", "Read the Equifax credit report for an 8-digit DNI; the result is {app}/results/check-bureau/latest."),
            new("score-risk", "Score an applicant with RiskSeal from JSON {email, phone, ip, dni}; the result is {app}/results/score-risk/latest."),
            new("send-otp", "Text a six-digit code to an E.164 phone; the result is {app}/results/send-otp/latest (never the code)."),
            new("create-payment", "Create a Monnet payment order in PEN from JSON {amount, reference, phone}; the result is {app}/results/create-payment/latest."),
            new("record-payment", "Read a Monnet payment by id and write it to Salesforce as a Loan_Transaction__c; the result is {app}/results/record-payment/latest."),
            new("status", "Summarize the last result of every operation into {app}/status."),
        ],
        Settings: [],
        Slots:
        [
            new("salesforce", "salesforce"),
            new("monnet", "monnet"),
            new("equifax", "equifax"),
            new("riskseal", "riskseal"),
            new("sms", "twilio"),
        ],
        Icon: "icon.svg");
}
