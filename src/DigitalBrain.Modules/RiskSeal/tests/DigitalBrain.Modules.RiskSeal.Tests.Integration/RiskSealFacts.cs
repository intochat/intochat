using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.RiskSeal;
using DigitalBrain.RiskSeal.Signals;
using Xunit;

namespace DigitalBrain.Modules.RiskSeal.Tests.Integration;

public sealed class RiskSealFacts(RiskSealHost host)
{
    private static readonly ScoreRequest Ana = new("ana@example.com", "+51987654321", "203.0.113.7", "DNI-1234");

    [Trait("host", "own")]
    [Fact]
    public async Task ADeploymentConnectionScoresWithTheIntegrationsKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeRiskSeal.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();
        var neuron = brain.Get<IRiskSeal>(await DeploymentAsync(brain));
        await using var scored = await brain.Observe<RiskScored>(neuron, ct);

        var score = await neuron.Score(Ana);

        Assert.Equal(["deploy-key"], fake.Bearers);
        var body = Assert.Single(fake.Bodies);
        Assert.Equal("ana@example.com", body.GetProperty("email").GetString());
        Assert.Equal("+51987654321", body.GetProperty("phone").GetString());
        Assert.Equal("203.0.113.7", body.GetProperty("ip").GetString());
        Assert.Equal("DNI-1234", body.GetProperty("document").GetString());
        Assert.Equal(score.RequestId, (await scored.NextAsync(ct: ct)).Score.RequestId);
        Assert.Equal(score.RequestId, Assert.Single(await neuron.ReadRecent(10)).RequestId);
    }

    [Fact]
    public async Task ScoreAndSignalsAreExtractedFromThePayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();

        var score = await brain.Get<IRiskSeal>(await DeploymentAsync(brain, person)).Score(new("ana@example.com", null));

        Assert.Equal("rs-1", score.RequestId);
        Assert.Equal(74, score.Score);
        Assert.Equal("high", score.RiskLevel);
        Assert.Equal([new RiskSignal("email_age_days", "3"), new RiskSignal("social_profiles", "0")], score.Signals);
        Assert.Equal(FakeRiskSeal.Payload, score.RawJson);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task APastedApiKeyConnectionScoresWithItsOwnKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeRiskSeal.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        fake.ValidKey = "user-key";
        var key = ConnectionKeys.For(brain.Owner.BrainId, "riskseal", "mine");
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IConnection>(key).Connect("api-key", "user-key");

        var score = await brain.Get<IRiskSeal>(key).Score(new(null, "+51987654321"));

        Assert.Equal("rs-1", score.RequestId);
        Assert.Equal(["user-key"], fake.Bearers);
        Assert.Equal(ConnectionStatus.Connected, (await brain.Get<IConnection>(key).Probe()).Status);
    }

    [Fact]
    public async Task ARefusedKeyPublishesRiskScoreFailedAndProbesFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        var key = ConnectionKeys.For(person.BrainId, "riskseal", "wrong");
        using var caller = person.AsCaller();
        await brain.Get<IConnection>(key).Connect("api-key", "wrong-key");
        var neuron = brain.Get<IRiskSeal>(key);
        await using var failed = await brain.Observe<RiskScoreFailed>(neuron, ct);

        await Assert.ThrowsAsync<RiskSealUnavailableException>(() => neuron.Score(Ana));

        var signal = await failed.NextAsync(ct: ct);
        Assert.Equal("unauthorized (401)", signal.Detail);
        Assert.DoesNotContain("wrong-key", signal.Detail, StringComparison.Ordinal);
        Assert.Empty(await neuron.ReadRecent(10));
        var view = await brain.Get<IConnection>(key).Probe();
        Assert.Equal(ConnectionStatus.Failing, view.Status);
        Assert.Equal("unauthorized (401)", view.Detail);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARequestWithoutEmailOrPhoneIsRefusedBeforeAnyCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeRiskSeal.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();
        var neuron = brain.Get<IRiskSeal>(await DeploymentAsync(brain));

        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Score(new(null, " ", "203.0.113.7")));
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Score(new(null, "987654321")));

        Assert.Empty(fake.Bearers);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeploymentConnectionProbesConnectedOrFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeRiskSeal.StartAsync(ct);
        await using var brain = await StartAsync(fake, ct);
        using var caller = brain.Owner.AsCaller();
        var connection = brain.Get<IConnection>(await DeploymentAsync(brain));

        Assert.Equal(ConnectionStatus.Connected, (await connection.Probe()).Status);
        fake.ValidKey = "rotated-key";
        var view = await connection.Probe();

        Assert.Equal(ConnectionStatus.Failing, view.Status);
        Assert.Equal("unauthorized (401)", view.Detail);
        Assert.Equal(["deploy-key", "deploy-key"], fake.Bearers);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeploymentConnectionWithTheIntegrationNotReadyFailsAsNotConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await FakeRiskSeal.StartAsync(ct);
        await using var brain = await TestBrain.Create()
            .WithModule<RiskSealModule, RiskSealModuleOptions>(options => { options.BaseUrl = fake.BaseUrl; options.AllowLoopback = true; })
            .StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var key = await DeploymentAsync(brain);
        var neuron = brain.Get<IRiskSeal>(key);
        await using var failed = await brain.Observe<RiskScoreFailed>(neuron, ct);

        var error = await Assert.ThrowsAsync<RiskSealUnavailableException>(() => neuron.Score(Ana));

        Assert.Equal("RiskSeal is not configured", error.Message);
        Assert.Equal("RiskSeal is not configured", (await failed.NextAsync(ct: ct)).Detail);
        var view = await brain.Get<IConnection>(key).Probe();
        Assert.Equal(ConnectionStatus.Failing, view.Status);
        Assert.Equal("RiskSeal is not configured", view.Detail);
        Assert.Empty(fake.Bearers);
    }

    // The caller must already be the owner.
    private static Task<string> DeploymentAsync(ITestBrain brain) => DeploymentAsync(brain, brain.Owner);

    private static async Task<string> DeploymentAsync(ITestBrain brain, Person person)
    {
        var key = ConnectionKeys.For(person.BrainId, "riskseal", "deployment");
        await brain.Get<IConnection>(key).Connect("deployment", "default");
        return key;
    }

    private static Task<ITestBrain> StartAsync(FakeRiskSeal fake, CancellationToken ct) => Compose(TestBrain.Create(), fake).StartAsync(ct);

    internal static TestBrain Compose(TestBrain brain, FakeRiskSeal fake)
        => brain
            .WithModule<RiskSealModule, RiskSealModuleOptions>(options => { options.BaseUrl = fake.BaseUrl; options.AllowLoopback = true; })
            .WithIntegration(RiskSealModule.Integration, "ApiKey", "deploy-key");
}
