using DigitalBrain.Equifax;
using DigitalBrain.Equifax.Signals;
using DigitalBrain.Framework.Platform.Connections;
using Xunit;

namespace DigitalBrain.Modules.Equifax.Tests;

public sealed class EquifaxFacts
{
    [Fact]
    public async Task ADeploymentConnectionReadsAReportWithTheIntegrationsClientId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();
        var equifax = await DeploymentAsync(brain);
        await using var read = await brain.Observe<BureauReportRead>(equifax, ct);

        var report = await equifax.ReadCreditReport(new("12345678", "loan"));

        Assert.Equal(["deploy-id:deploy-secret"], fake.TokenClients);
        Assert.Equal(["Bearer token-1"], fake.ReportBearers);
        Assert.Equal("12345678", report.DocumentNumber);
        Assert.Equal(report, (await read.NextAsync(ct: ct)).Report);
        Assert.Equal([report], await equifax.ReadRecent(10));
    }

    [Fact]
    public async Task APastedCredentialsConnectionReadsWithItsOwnClient()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "equifax", "c1");
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IConnection>(key).Connect("credentials", $"ClientId=mine;ClientSecret=my-secret;BaseUrl={fake.Origin}");

        var report = await brain.Get<IEquifax>(key).ReadCreditReport(new("87654321"));

        Assert.Equal(["mine:my-secret"], fake.TokenClients);
        Assert.Equal("87654321", report.DocumentNumber);
        Assert.Equal(ConnectionStatus.Connected, (await brain.Get<IConnection>(key).Probe()).Status);
    }

    [Fact]
    public async Task TheTokenIsReusedAcrossTwoReads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();
        var equifax = await DeploymentAsync(brain);

        await equifax.ReadCreditReport(new("12345678"));
        await equifax.ReadCreditReport(new("11112222"));

        Assert.Single(fake.TokenClients);
        Assert.Equal(["Bearer token-1", "Bearer token-1"], fake.ReportBearers);
        Assert.Equal(["11112222", "12345678"], (await equifax.ReadRecent(10)).Select(report => report.DocumentNumber));
    }

    [Fact]
    public async Task ARefusedTokenPublishesBureauReportFailedAndTheProbeFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        fake.RefuseToken = true;
        var key = ConnectionKeys.For(brain.Owner.BrainId, "equifax", "c1");
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IConnection>(key).Connect("credentials", $"ClientId=wrong;ClientSecret=nope;BaseUrl={fake.Origin}");
        var equifax = brain.Get<IEquifax>(key);
        await using var failed = await brain.Observe<BureauReportFailed>(equifax, ct);

        var error = await Assert.ThrowsAsync<EquifaxUnavailableException>(() => equifax.ReadCreditReport(new("12345678")));

        Assert.Equal("unauthorized (401)", error.Message);
        var signal = await failed.NextAsync(ct: ct);
        Assert.Equal(("12345678", "unauthorized (401)"), (signal.DocumentNumber, signal.Detail));
        Assert.DoesNotContain("nope", signal.Detail, StringComparison.Ordinal);
        var view = await brain.Get<IConnection>(key).Probe();
        Assert.Equal(ConnectionStatus.Failing, view.Status);
        Assert.Equal("unauthorized (401)", view.Detail);
        Assert.Empty(fake.ReportBearers);
    }

    [Theory]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("1234567a")]
    [InlineData("")]
    public async Task ADocumentThatIsNotAnEightDigitDniIsRefusedBeforeAnyCall(string document)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();
        var equifax = await DeploymentAsync(brain);

        await Assert.ThrowsAsync<ArgumentException>(() => equifax.ReadCreditReport(new(document)));

        Assert.Empty(fake.TokenClients);
        Assert.Empty(fake.ReportBearers);
    }

    [Fact]
    public async Task TheLoanFlagsAreExtractedFromThePayload()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();

        var report = await (await DeploymentAsync(brain)).ReadCreditReport(new("12345678"));

        Assert.Equal(new BureauFlags(true, "CPP", 45, false, true, false), report.Flags);
        Assert.Contains("\"infocorp\"", report.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeploymentConnectionProbesFailingWhenTheTokenIsRefusedAndConnectedWithTheIntegration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();
        await DeploymentAsync(brain);
        var connection = brain.Get<IConnection>(DeploymentKey(brain));

        fake.RefuseToken = true;
        var view = await connection.Probe();
        Assert.Equal((ConnectionStatus.Failing, "unauthorized (401)"), (view.Status, view.Detail));

        fake.RefuseToken = false;
        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        Assert.Equal(["deploy-id:deploy-secret", "deploy-id:deploy-secret"], fake.TokenClients);
    }

    [Fact]
    public async Task ADeploymentConnectionWithTheIntegrationNotReadyFailsWithEquifaxIsNotConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeEquifax.StartAsync(ct);
        await using var brain = await TestBrain.Create()
            .WithModule<EquifaxModule, EquifaxModuleOptions>(options => { options.BaseUrl = fake.Origin; options.AllowLoopback = true; })
            .WithIntegration(EquifaxModule.Integration, "ClientId", "deploy-id")
            .StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var equifax = await DeploymentAsync(brain);
        await using var failed = await brain.Observe<BureauReportFailed>(equifax, ct);

        var error = await Assert.ThrowsAsync<EquifaxUnavailableException>(() => equifax.ReadCreditReport(new("12345678")));

        Assert.Equal("Equifax is not configured", error.Message);
        var signal = await failed.NextAsync(ct: ct);
        Assert.Equal(("12345678", "Equifax is not configured"), (signal.DocumentNumber, signal.Detail));
        var view = await brain.Get<IConnection>(DeploymentKey(brain)).Probe();
        Assert.Equal((ConnectionStatus.Failing, "Equifax is not configured"), (view.Status, view.Detail));
        Assert.Empty(fake.TokenClients);
    }

    [Fact]
    public void AMissingFieldLeavesItsFlagNull()
        => Assert.Equal(new BureauFlags(null, "NOR", null, null, null, null), BureauFlagsReader.Read("""{"sbs":{"rating":"NOR","maxDaysDelay":"x"}}"""));

    private static string DeploymentKey(ITestBrain brain) => ConnectionKeys.For(brain.Owner.BrainId, "equifax", "deployment");

    private static async Task<IEquifax> DeploymentAsync(ITestBrain brain)
    {
        await brain.Get<IConnection>(DeploymentKey(brain)).Connect("deployment", "default");
        return brain.Get<IEquifax>(DeploymentKey(brain));
    }

    private static Task<ITestBrain> StartAsync(FakeEquifax fake, CancellationToken ct)
        => TestBrain.Create()
            .WithModule<EquifaxModule, EquifaxModuleOptions>(options => { options.BaseUrl = fake.Origin; options.AllowLoopback = true; })
            .WithIntegration(EquifaxModule.Integration, "ClientId", "deploy-id")
            .WithIntegration(EquifaxModule.Integration, "ClientSecret", "deploy-secret")
            .StartAsync(ct);
}
