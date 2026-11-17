using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Orleans.Brain;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Hosting;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration;

[DefaultNeuronType("test.settings")]
public interface ITestUserSettings
{
    Task<string> SetProfile(string name);
    Task<string> Read();
}

[Neuron("test.settings", State = "settings")]
public sealed class TestUserSettingsNeuron : Neuron<TestUserSettingsState>, ITestUserSettings
{
    public Task<string> SetProfile(string name)
    {
        State.Name = name;
        return Task.FromResult(name);
    }

    public Task<string> Read() => Task.FromResult(State.Name);
}

[GenerateSerializer]
public sealed class TestUserSettingsState
{
    [Id(0)]
    public string Name { get; set; } = string.Empty;
}

[Module("test-filter-module")]
public sealed class TestFilterModule : Module
{
    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [Need.State("settings")],
        Settings: [],
        Secrets: []);
}

public sealed class PlainNeuronCallFilterFacts(PlainNeuronCallFilterFacts.FilterSiloFixture fixture)
    : IClassFixture<PlainNeuronCallFilterFacts.FilterSiloFixture>
{
    private EdgeAuthority Edge => fixture.Services.GetRequiredService<EdgeAuthority>();

    public sealed class FilterSiloFixture : IAsyncLifetime
    {
        private IInProcessHost? _host;
        private int _counter;

        public IServiceProvider Services => (_host ?? throw new InvalidOperationException("Silo host is not started.")).Services;

        public string NewBrain(string prefix)
            => $"{prefix}{Interlocked.Increment(ref _counter)}";

        public async ValueTask InitializeAsync()
        {
            var signalAssemblies = new[]
            {
                typeof(FilterSiloFixture).Assembly,
                typeof(TestFilterModule).Assembly,
            }.Distinct().ToArray();

            var factory = new LocalSiloHostFactory(
                new BrainStorageRegistration(signalAssemblies));

            _host = await factory.Start(
                new InProcessHostOptions
                {
                    Modules =
                    [
                        typeof(TestFilterModule),
                    ],
                },
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task UnstampedCallToPlainNeuronThrowsUnauthorizedAccessException()
    {
        var brain = fixture.NewBrain("unstamped");
        var settingsNeuron = SiloNeurons.Get<ITestUserSettings>(fixture.Services, $"{brain}/test.settings/default");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await settingsNeuron.SetProfile("Hacker");
        });
    }

    [Fact]
    public async Task StampedCallToPlainNeuronSucceeds()
    {
        var brain = fixture.NewBrain("stamped");
        using var stamp = Edge.Enter(Edge.Person(brain, brain, new BrainId(brain)));
        var settingsNeuron = SiloNeurons.Get<ITestUserSettings>(fixture.Services, $"{brain}/test.settings/default");

        var state = await settingsNeuron.SetProfile("Ada");
        Assert.NotNull(state);
        Assert.Equal("Ada", state);
    }

    [Fact]
    public async Task InternalPlumbingContractRunsOnlyAsTheKernel()
    {
        var brain = fixture.NewBrain("plumbing");
        var announcer = SiloNeurons.Get<IBrainAnnouncer>(fixture.Services, $"{brain}/main");

        // The announcer's members are one-way, so a refusal of another caller is not observable here; the kernel's call runs.
        using (Edge.EnterKernel(new BrainId(brain)))
        {
            Assert.Null(await Record.ExceptionAsync(() => announcer.InstanceCreated("test.type", "test.key")));
        }
    }

    [Fact]
    public async Task UnstampedReadOnlyCall_ThrowsUnauthorizedAccessException()
    {
        var brain = fixture.NewBrain("unstamped-read");
        var settingsNeuron = SiloNeurons.Get<ITestUserSettings>(fixture.Services, $"{brain}/test.settings/default");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(settingsNeuron.Read);
    }

    [Fact]
    public async Task UnstampedCall_WithAForgedRequestContextKey_StillThrowsUnauthorizedAccessException()
    {
        var brain = fixture.NewBrain("unstamped-forged-module");
        var settingsNeuron = SiloNeurons.Get<ITestUserSettings>(fixture.Services, $"{brain}/test.settings/default");

        global::Orleans.Runtime.RequestContext.Set("intochatos.trusted-module-call", true);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await settingsNeuron.SetProfile("Forged");
            });
        }
        finally
        {
            global::Orleans.Runtime.RequestContext.Remove("intochatos.trusted-module-call");
        }
    }

    [Fact]
    public async Task StampedCall_FromForeignBrain_ThrowsUnauthorizedAccessException()
    {
        var targetBrain = fixture.NewBrain("victim-brain");
        var attackerBrain = fixture.NewBrain("attacker-brain");

        using var stamp = Edge.Enter(Edge.Person(attackerBrain, attackerBrain, new BrainId(attackerBrain)));
        var settingsNeuron = SiloNeurons.Get<ITestUserSettings>(fixture.Services, $"{targetBrain}/test.settings/default");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await settingsNeuron.SetProfile("Intruder");
        });
    }

    [Fact]
    public async Task StampedCall_ReadOnlyMethod_SucceedsForLegitimateCaller()
    {
        var brain = fixture.NewBrain("stamped-read");
        using var stamp = Edge.Enter(Edge.Person(brain, brain, new BrainId(brain)));
        var settingsNeuron = SiloNeurons.Get<ITestUserSettings>(fixture.Services, $"{brain}/test.settings/default");

        await settingsNeuron.SetProfile("Ada");
        var state = await settingsNeuron.Read();
        Assert.NotNull(state);
        Assert.Equal("Ada", state);
    }
}
