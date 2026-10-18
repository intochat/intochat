using System.Net;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Twilio;
using DigitalBrain.Twilio.Signals;
using Xunit;

namespace DigitalBrain.Modules.Twilio.Tests.Integration;

public sealed class TwilioFacts(TwilioHost shared)
{
    private const string DeploySid = "ACdeploy";
    private const string DeployToken = "deploy-token";
    private const string DeployFrom = "+15005550006";
    private const string Credentials = "AccountSid=ACmine;AuthToken=mine-token;From=+15550001111";
    private const string PublicOrigin = "https://brain.example/";

    [Trait("host", "own")]
    [Fact]
    public async Task ADeploymentConnectionSendsWithTheIntegrationsAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        var sms = await host.DeploymentAsync();
        await using var sent = await host.Brain.Observe<SmsSent>(sms, ct);

        using var caller = host.Brain.Owner.AsCaller();
        var message = await sms.Send(new("+51987654321", "Your code is 123456", "otp-1"));

        var request = Assert.Single(host.Fake.Sends);
        Assert.Equal($"/2010-04-01/Accounts/{DeploySid}/Messages.json", request.Path);
        Assert.Equal(FakeTwilio.Basic(DeploySid, DeployToken), request.Authorization);
        Assert.Equal("+51987654321", request.Form["To"]);
        Assert.Equal(DeployFrom, request.Form["From"]);
        Assert.Equal("Your code is 123456", request.Form["Body"]);
        Assert.Equal(host.StatusUrl, request.Form["StatusCallback"]);
        Assert.EndsWith($"/twilio/status/{host.Brain.Owner.BrainId}", request.Form["StatusCallback"], StringComparison.Ordinal);
        Assert.Equal("queued", message.Status);
        Assert.Equal(message, (await sent.NextAsync(ct: ct)).Message);
        Assert.Equal([message], await sms.ReadRecent(10));
        Assert.Equal(DeploySid, (await host.Brain.Get<IConnection>(host.DeploymentKey).Read()).Account);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeploymentConnectionProbesConnectedOrFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        await host.DeploymentAsync();
        using var caller = host.Brain.Owner.AsCaller();
        var connection = host.Brain.Get<IConnection>(host.DeploymentKey);

        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        host.Fake.Answer = FakeTwilio.Mode.Unauthorized;
        var failing = await connection.Probe();

        Assert.Equal(ConnectionStatus.Failing, failing.Status);
        Assert.All(host.Fake.Requests, r => Assert.Equal(FakeTwilio.Basic(DeploySid, DeployToken), r.Authorization));
        Assert.Contains(host.Fake.Requests, r => r.Path == $"/2010-04-01/Accounts/{DeploySid}.json");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task APastedCredentialsConnectionSendsWithItsOwnAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        var key = await host.ConnectAsync();
        using var caller = host.Brain.Owner.AsCaller();

        var message = await host.Brain.Get<ITwilioSms>(key).Send(new("+51987654321", "hello"));

        var request = Assert.Single(host.Fake.Sends);
        Assert.Equal("/2010-04-01/Accounts/ACmine/Messages.json", request.Path);
        Assert.Equal(FakeTwilio.Basic("ACmine", "mine-token"), request.Authorization);
        Assert.Equal("+15550001111", request.Form["From"]);
        Assert.StartsWith("SM", message.Sid, StringComparison.Ordinal);
        var view = await host.Brain.Get<IConnection>(key).Read();
        Assert.Equal("ACmine", view.Account);
        Assert.DoesNotContain("mine-token", view.ToString(), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARepeatedClientReferenceDoesNotSendTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        var sms = await host.DeploymentAsync();
        using var caller = host.Brain.Owner.AsCaller();

        var first = await sms.Send(new("+51987654321", "Your code is 123456", "otp-dedup"));
        var again = await sms.Send(new("+51987654321", "Your code is 123456", "otp-dedup"));

        Assert.Single(host.Fake.Sends);
        Assert.Equal(first, again);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnauthorizedAccountYieldsSmsFailedAndAFailingProbe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        var key = await host.ConnectAsync();
        host.Fake.Answer = FakeTwilio.Mode.Unauthorized;
        using var caller = host.Brain.Owner.AsCaller();
        var sms = host.Brain.Get<ITwilioSms>(key);
        await using var failed = await host.Brain.Observe<SmsFailed>(sms, ct);

        var message = await sms.Send(new("+51987654321", "hello"));

        Assert.Equal("failed", message.Status);
        var signal = await failed.NextAsync(ct: ct);
        Assert.Equal("+51987654321", signal.To);
        Assert.Contains("unauthorized (401)", signal.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("mine-token", signal.Detail, StringComparison.Ordinal);
        var probe = await host.Brain.Get<IConnection>(key).Probe();
        Assert.Equal(ConnectionStatus.Failing, probe.Status);
        Assert.Contains(host.Fake.Requests, r => r.Path == "/2010-04-01/Accounts/ACmine.json");
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnInvalidNumberRefusalCarriesTwiliosErrorCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        var sms = await host.DeploymentAsync();
        host.Fake.Answer = FakeTwilio.Mode.InvalidNumber;
        await using var failed = await host.Brain.Observe<SmsFailed>(sms, ct);
        using var caller = host.Brain.Owner.AsCaller();

        var message = await sms.Send(new("+51987654321", "hello", "otp-refused"));

        Assert.Equal("21211", message.ErrorCode);
        Assert.Contains("21211", (await failed.NextAsync(ct: ct)).Detail, StringComparison.Ordinal);
        Assert.Empty(await sms.ReadRecent(10));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnavailableTwilioPublishesSmsFailedThenThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        var sms = await host.DeploymentAsync();
        host.Fake.Answer = FakeTwilio.Mode.Down;
        await using var failed = await host.Brain.Observe<SmsFailed>(sms, ct);
        using var caller = host.Brain.Owner.AsCaller();

        await Assert.ThrowsAsync<TwilioUnavailableException>(() => sms.Send(new("+51987654321", "hello")));

        Assert.Contains("503", (await failed.NextAsync(ct: ct)).Detail, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData("987654321")]
    [InlineData("+0987654321")]
    [InlineData("+51 987 654 321")]
    public async Task ABadE164NumberIsRefusedBeforeAnyCall(string to)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.StartAsync(ct);
        var sms = await host.DeploymentAsync();
        using var caller = host.Brain.Owner.AsCaller();

        await Assert.ThrowsAsync<ArgumentException>(() => sms.Send(new(to, "hello")));
        await Assert.ThrowsAsync<ArgumentException>(() => sms.Send(new("+51987654321", new string('x', 1601))));

        Assert.DoesNotContain(host.Fake.Requests, r => r.Path.EndsWith("/Messages.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASignedStatusCallbackForTheBrainPublishesSmsDeliveredOnceEvenWhenDeliveredTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.ForPersonAsync(shared, ct);
        var sms = await host.DeploymentAsync();
        SmsMessage message;
        using (host.Owner.AsCaller()) { message = await sms.Send(new("+51987654321", "hello")); }
        await using var delivered = await host.Brain.Observe<SmsDelivered>(sms, ct);

        using var first = await host.PostStatusAsync(DeployToken, DeploySid, message.Sid, "delivered", ct);
        using var repeat = await host.PostStatusAsync(DeployToken, DeploySid, message.Sid, "delivered", ct);
        using var later = await host.PostStatusAsync(DeployToken, DeploySid, message.Sid, "read", ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        Assert.Equal(new SmsDelivered(message.Sid, "delivered"), await delivered.NextAsync(ct: ct) with { Publisher = default, Trace = default });
        // The repeat published nothing: the next signal is the later status.
        Assert.Equal("read", (await delivered.NextAsync(ct: ct)).Status);
        using var caller = host.Owner.AsCaller();
        Assert.Equal("read", Assert.Single(await sms.ReadRecent(1)).Status);
    }

    [Fact]
    public async Task AConnectionReceivesItsOwnAccountsStatusCallbacks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.ForPersonAsync(shared, ct);
        var key = await host.ConnectAsync();
        SmsMessage message;
        using (host.Owner.AsCaller()) { message = await host.Brain.Get<ITwilioSms>(key).Send(new("+51987654321", "hello")); }
        await using var failed = await host.Brain.Observe<SmsFailed>(host.Brain.Get<ITwilioSms>(key), ct);

        using var response = await host.PostStatusAsync("mine-token", "ACmine", message.Sid, "undelivered", ct, errorCode: "30003");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var signal = await failed.NextAsync(ct: ct);
        Assert.Equal("+51987654321", signal.To);
        Assert.Equal("undelivered (Twilio error 30003)", signal.Detail);
    }

    [Fact]
    public async Task AnUnknownBrainIdIs404AForgedSignatureIs403AndAnotherAccountSidPublishesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await Host.ForPersonAsync(shared, ct);
        var sms = await host.DeploymentAsync();
        SmsMessage message;
        using (host.Owner.AsCaller()) { message = await sms.Send(new("+51987654321", "hello")); }

        using var badBrain = await host.PostStatusAsync(DeployToken, DeploySid, message.Sid, "delivered", ct, brainId: "@nobody");
        using var forged = await host.PostStatusAsync("not-the-token", DeploySid, message.Sid, "delivered", ct);
        using var unsigned = await host.PostStatusAsync(null, DeploySid, message.Sid, "delivered", ct);
        using var other = await host.PostStatusAsync(DeployToken, "ACnobody", message.Sid, "delivered", ct);

        Assert.Equal(HttpStatusCode.NotFound, badBrain.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using var caller = host.Owner.AsCaller();
        Assert.Equal("queued", Assert.Single(await sms.ReadRecent(1)).Status);
    }

    [Fact]
    public void TheSignatureIsHmacSha1OverTheUrlAndTheSortedFields()
    {
        // Reference value from an independent HMAC-SHA1 over url + sorted name/value pairs (Twilio's documented algorithm).
        TwilioFormField[] fields = [new("CallSid", "CA1234567890ABCDE"), new("Caller", "+12349013030"), new("Digits", "1234"), new("From", "+12349013030"), new("To", "+18005551212")];
        Assert.Equal("vNe7KK2kJwCsxc9K3OLkkKB3qqI=", TwilioSignature.Compute("12345", "https://example.com/myapp.php?foo=1&bar=2", fields));
    }

    internal static TestBrain Compose(TestBrain brain, FakeTwilio fake) => Host.Compose(brain, fake);

    private sealed class Host(ITestBrain brain, FakeTwilio fake, Person? person = null) : IAsyncDisposable
    {
        public ITestBrain Brain => brain;
        public FakeTwilio Fake => fake;
        public Person Owner => person ?? brain.Owner;
        public string DeploymentKey => ConnectionKeys.For(Owner.BrainId, "twilio", "deployment");
        public string StatusUrl => StatusUrlFor(Owner.BrainId);

        private static string StatusUrlFor(string brainId) => $"{PublicOrigin}twilio/status/{Uri.EscapeDataString(brainId)}";

        public static async Task<Host> StartAsync(CancellationToken ct)
        {
            var fake = await FakeTwilio.StartAsync(ct);
            return new Host(await Compose(TestBrain.Create(), fake).StartAsync(ct), fake);
        }

        // A fresh person on the shared host, whose fake is the host's.
        public static async Task<Host> ForPersonAsync(TwilioHost shared, CancellationToken ct)
            => new(shared.Brain, shared.Fake, await shared.PersonAsync(ct: ct));

        public static TestBrain Compose(TestBrain brain, FakeTwilio fake)
            => brain
                .WithModule<TwilioModule, TwilioModuleOptions>(options => { options.BaseUrl = fake.Origin; options.AllowLoopback = true; })
                .WithIntegration(TwilioModule.Integration, "AccountSid", DeploySid)
                .WithIntegration(TwilioModule.Integration, "AuthToken", DeployToken)
                .WithIntegration(TwilioModule.Integration, "FromNumber", DeployFrom)
                .WithConfiguration(DigitalBrainConfiguration.ConnectionsPublicOriginKey, PublicOrigin);

        public async Task<ITwilioSms> DeploymentAsync()
        {
            using var caller = Owner.AsCaller();
            var view = await brain.Get<IConnection>(DeploymentKey).Connect("deployment", "default");
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            return brain.Get<ITwilioSms>(DeploymentKey);
        }

        public async Task<string> ConnectAsync()
        {
            var key = ConnectionKeys.For(Owner.BrainId, "twilio", "sms");
            using var caller = Owner.AsCaller();
            var view = await brain.Get<IConnection>(key).Connect("credentials", Credentials);
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            return key;
        }

        // Twilio's delivery: form-encoded, signed over the public URL it was told to call.
        public async Task<HttpResponseMessage> PostStatusAsync(string? token, string accountSid, string messageSid, string status, CancellationToken ct, string? errorCode = null, string? brainId = null)
        {
            brainId ??= Owner.BrainId;
            var form = new Dictionary<string, string> { ["AccountSid"] = accountSid, ["MessageSid"] = messageSid, ["MessageStatus"] = status, ["To"] = "+51987654321" };
            if (errorCode is not null) { form["ErrorCode"] = errorCode; }
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/twilio/status/{Uri.EscapeDataString(brainId)}") { Content = new FormUrlEncodedContent(form) };
            if (token is not null)
            { request.Headers.Add("X-Twilio-Signature", TwilioSignature.Compute(token, StatusUrlFor(brainId), form.Select(f => new TwilioFormField(f.Key, f.Value)))); }
            return await brain.HttpClient.SendAsync(request, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (person is not null) { return; }
            await brain.DisposeAsync();
            await fake.DisposeAsync();
        }
    }
}
