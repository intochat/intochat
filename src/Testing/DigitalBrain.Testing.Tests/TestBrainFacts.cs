using System.Net;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Orleans;
using DigitalBrain.Testing.Neurons;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Testing.Tests;

public sealed class TestBrainFacts
{
    [Fact]
    public async Task AnInProcessBrainAnswersHttpAndGrainsFromOneProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        using var ready = await brain.HttpClient.GetAsync("/ready", ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var pulse = brain.Get<IPulse>("owner/pulse");
        await using var pulsed = await brain.Observe<Pulsed>(pulse, ct);
        await pulse.Pulse(7);
        Assert.Equal(7, (await pulsed.NextAsync(ct: ct)).Value);
        Assert.Same(brain.Services.GetRequiredService<MemoryStores>(), brain.Services.GetRequiredService<MemoryStores>());
    }

    [Fact]
    public async Task ARestartKeepsNeuronStateAndTheHttpAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        var address = brain.HttpClient.BaseAddress;
        var stores = brain.Services.GetRequiredService<MemoryStores>();
        await brain.Get<IPulse>("owner/pulse").Pulse(3);
        await brain.RestartAsync(ct);
        Assert.Equal(address, brain.HttpClient.BaseAddress);
        Assert.Same(stores, brain.Services.GetRequiredService<MemoryStores>());
        var retained = await brain.Get<IPulse>("owner/pulse").Retained(nameof(Pulsed), 0);
        Assert.Equal(3, Assert.IsType<Pulsed>(Assert.Single(retained).Signal).Value);
    }

    [Fact]
    public async Task ADeactivatedNeuronReloadsItsStateOnTheNextCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        var pulse = brain.Get<IPulse>("owner/pulse");
        await pulse.Pulse(5);
        await brain.DeactivateAsync(pulse, ct);
        Assert.Single(await pulse.Retained(nameof(Pulsed), 0));
    }

    [Fact]
    public async Task TwoBrainsRunSideBySideInOneProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        await using var second = await TestBrain.Create().WithModule<PulseModule>().StartAsync(ct);
        await first.Get<IPulse>("owner/pulse").Pulse(1);
        Assert.Empty(await second.Get<IPulse>("owner/pulse").Retained(nameof(Pulsed), 0));
    }

    [Fact]
    public async Task WithIntegrationLandsInTheSiloConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>()
            .WithIntegration(IntegrationSchema.For("probe", "Probe").RequiresSecret("Token"), "Token", "t-1").StartAsync(ct);
        Assert.Equal("t-1", brain.Services.GetRequiredService<IConfiguration>()[IntegrationSchema.Key("probe", "Token")]);
    }
}
