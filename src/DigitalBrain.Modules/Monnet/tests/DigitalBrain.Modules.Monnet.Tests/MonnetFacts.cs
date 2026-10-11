using System.Net;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Monnet;
using DigitalBrain.Monnet.Signals;
using Xunit;

namespace DigitalBrain.Modules.Monnet.Tests;

public sealed class MonnetFacts
{
    private static readonly CreatePaymentOrder Order = new(120.50m, "loan-1", "First instalment", PayerDni: "12345678");

    private static object Paid(string merchant, string paymentId = "pay-1")
        => new { merchantId = merchant, paymentId, reference = "loan-1", amount = 120.50m, currency = "PEN", status = "paid" };

    [Fact]
    public async Task ADeploymentConnectionCreatesAnOrderWithTheIntegrationsKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var deployment = await monnet.ConnectDeploymentAsync();
        using var caller = monnet.Owner.AsCaller();
        await using var created = await monnet.Brain.Observe<PaymentOrderCreated>(deployment, ct);

        var order = await deployment.CreatePaymentOrder(Order);

        Assert.Equal(("loan-1", 120.50m, "PEN"), (order.Reference, order.Amount, order.Currency));
        Assert.Equal(order, (await created.NextAsync(ct: ct)).Order);
        Assert.Equal(("/v1/payments", "deployment-key", MonnetBrain.DeploymentMerchant), Assert.Single(monnet.Fake.Requests));
        Assert.Equal(MonnetBrain.DeploymentMerchant, (await monnet.Brain.Get<IConnection>(monnet.DeploymentKey).Read()).Account);
    }

    [Fact]
    public async Task ADeploymentConnectionProbesConnectedOrFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        await monnet.ConnectDeploymentAsync();
        using var caller = monnet.Owner.AsCaller();
        var connection = monnet.Brain.Get<IConnection>(monnet.DeploymentKey);

        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        monnet.Fake.Keys.Clear();
        var refused = await connection.Probe();

        Assert.Equal(ConnectionStatus.Failing, refused.Status);
        Assert.Equal("unauthorized (401)", refused.Detail);
        Assert.Equal("deployment-key", monnet.Fake.Requests[^1].ApiKey);
    }

    [Fact]
    public async Task APastedCredentialsConnectionCreatesAnOrderWithItsOwnKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var shop = await monnet.ConnectAsync("MerchantId=m-shop;ApiKey=pasted-key");
        using var caller = monnet.Owner.AsCaller();

        var order = await shop.CreatePaymentOrder(Order);

        Assert.StartsWith("pay-", order.PaymentId, StringComparison.Ordinal);
        Assert.Equal(("/v1/payments", "pasted-key", "m-shop"), Assert.Single(monnet.Fake.Requests));
        var view = await monnet.Brain.Get<IConnection>(monnet.ConnectionKey).Read();
        Assert.Equal("m-shop", view.Account);
        Assert.DoesNotContain("pasted-key", view.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedKeyPublishesPaymentFailedWithTheProviderStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var deployment = await monnet.ConnectDeploymentAsync();
        monnet.Fake.Keys.Clear();
        using var caller = monnet.Owner.AsCaller();
        await using var failed = await monnet.Brain.Observe<PaymentFailed>(deployment, ct);

        var error = await Assert.ThrowsAsync<MonnetUnavailableException>(() => deployment.CreatePaymentOrder(Order));

        Assert.Equal("unauthorized (401)", error.Message);
        var signal = await failed.NextAsync(ct: ct);
        Assert.Equal(("loan-1", "unauthorized (401)"), (signal.Reference, signal.Detail));
    }

    [Fact]
    public async Task ReadingAPaymentAsksMonnetByItsId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var deployment = await monnet.ConnectDeploymentAsync();
        using var caller = monnet.Owner.AsCaller();

        var status = await deployment.ReadPayment(new("pay-9"));

        Assert.Equal(("pay-9", "paid"), (status.PaymentId, status.Status));
        Assert.Equal("/v1/payments/pay-9", Assert.Single(monnet.Fake.Requests).Path);
    }

    [Fact]
    public async Task AWebhookForTheBrainReachesItsConnectedMerchantOnceEvenWhenDeliveredTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var deployment = await monnet.ConnectDeploymentAsync();
        var shop = await monnet.ConnectAsync("MerchantId=m-shop;ApiKey=pasted-key");
        using var caller = monnet.Owner.AsCaller();
        await using var received = await monnet.Brain.Observe<PaymentReceived>(deployment, ct);

        using (var first = await monnet.DeliverAsync(Paid(MonnetBrain.DeploymentMerchant), ct)) { Assert.Equal(HttpStatusCode.Accepted, first.StatusCode); }
        using (var second = await monnet.DeliverAsync(Paid(MonnetBrain.DeploymentMerchant), ct)) { Assert.Equal(HttpStatusCode.Accepted, second.StatusCode); }

        var signal = await received.NextAsync(ct: ct);
        Assert.Equal(("pay-1", "loan-1", 120.50m), (signal.Receipt.PaymentId, signal.Receipt.Reference, signal.Receipt.Amount));
        Assert.Single(await deployment.ReadReceived(10));
        Assert.Single(received.Snapshot);
        Assert.Empty(await shop.ReadReceived(10));
    }

    [Fact]
    public async Task AWebhookReachesAMerchantWhoseOutboundProbeFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var deployment = await monnet.ConnectDeploymentAsync();
        using var caller = monnet.Owner.AsCaller();
        monnet.Fake.Keys.Clear();
        Assert.Equal(ConnectionStatus.Failing, (await monnet.Brain.Get<IConnection>(monnet.DeploymentKey).Probe()).Status);
        await using var received = await monnet.Brain.Observe<PaymentReceived>(deployment, ct);

        using (var delivery = await monnet.DeliverAsync(Paid(MonnetBrain.DeploymentMerchant), ct)) { Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode); }

        Assert.Equal("pay-1", (await received.NextAsync(ct: ct)).Receipt.PaymentId);
        Assert.Single(await deployment.ReadReceived(10));
    }

    [Fact]
    public async Task AWebhookForAnUnknownBrainIdIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);

        using var response = await monnet.DeliverAsync(Paid(MonnetBrain.DeploymentMerchant), ct, brainId: "@not-a-brain");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AWebhookForAnotherMerchantIdIsAcceptedButPublishesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var deployment = await monnet.ConnectDeploymentAsync();
        using var caller = monnet.Owner.AsCaller();

        using var response = await monnet.DeliverAsync(Paid("m-stranger"), ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await deployment.ReadReceived(10));
    }

    [Fact]
    public async Task AWebhookWithAWrongSignatureIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        var deployment = await monnet.ConnectDeploymentAsync();
        using var caller = monnet.Owner.AsCaller();

        using var response = await monnet.DeliverAsync(Paid(MonnetBrain.DeploymentMerchant), ct, signature: new string('0', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await deployment.ReadReceived(10));
    }

    [Fact]
    public async Task WithoutAWebhookSecretEveryDeliveryIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct, withWebhookSecret: false);

        using var response = await monnet.DeliverAsync(new { merchantId = MonnetBrain.DeploymentMerchant, paymentId = "pay-1" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AProbeAsksMonnetWhetherThePastedKeyWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);
        await monnet.ConnectAsync("MerchantId=m-shop;ApiKey=pasted-key");
        using var caller = monnet.Owner.AsCaller();
        var connection = monnet.Brain.Get<IConnection>(monnet.ConnectionKey);

        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        monnet.Fake.Keys.Clear();
        var refused = await connection.Probe();

        Assert.Equal(ConnectionStatus.Failing, refused.Status);
        Assert.Equal("unauthorized (401)", refused.Detail);
    }

    [Fact]
    public async Task AMalformedPasteIsRefusedAndLeftFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monnet = await MonnetBrain.StartAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(() => monnet.ConnectAsync("just-a-key"));

        using var caller = monnet.Owner.AsCaller();
        var view = await monnet.Brain.Get<IConnection>(monnet.ConnectionKey).Read();
        Assert.Equal(ConnectionStatus.Failing, view.Status);
        Assert.DoesNotContain("just-a-key", view.ToString(), StringComparison.Ordinal);
    }
}
