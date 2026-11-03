using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Capabilities;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Authority;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition.Confirmations;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.Kernel;

public sealed class PlainNeuronCallFilterFacts(PlainNeuronCallFilterFacts.FilterSiloFixture fixture)
    : IClassFixture<PlainNeuronCallFilterFacts.FilterSiloFixture>
{
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
                typeof(AppsModule).Assembly,
                typeof(IApp).Assembly,
                typeof(AuthorityPlainNeurons).Assembly,
                typeof(IConnectionRequests).Assembly,
                typeof(ConfirmationNeurons).Assembly,
            }.Distinct().ToArray();

            var factory = new LocalSiloHostFactory(
                new BrainStorageRegistration(signalAssemblies),
                services =>
                {
                    services.TryAddSingleton<ICallFilter, CallFilter>();
                    services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, PlainNeuronCallFilter>());
                });

            _host = await factory.Start(
                new InProcessHostOptions
                {
                    Modules =
                    [
                        typeof(AppsModule),
                        typeof(AuthorityPlainNeurons),
                        typeof(ConfirmationNeurons),
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
        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");

        // F5 requirement: PlainNeuronCallFilter must fail-closed on unstamped calls to non-plumbing neurons
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await settingsNeuron.SetProfile("Hacker", "dark", "gpt-5");
        });
    }

    [Fact]
    public async Task StampedCallToPlainNeuronSucceeds()
    {
        var brain = fixture.NewBrain("stamped");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));
        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");

        var state = await settingsNeuron.SetProfile("Ada", "dark", "gpt-5");
        Assert.NotNull(state);
        Assert.Equal("Ada", state.DisplayName);
    }

    [Fact]
    public async Task InternalPlumbingContractExecutesCleanlyWithoutCallerStamp()
    {
        var brain = fixture.NewBrain("plumbing");
        var announcer = SiloNeurons.Get<IBrainAnnouncer>(fixture.Services, $"{brain}/kernel.announcer");

        // IBrainAnnouncer is marked [DigitalBrain.Access.Internal] and is recognized as plumbing
        var exception = await Record.ExceptionAsync(async () =>
        {
            await announcer.InstanceCreated("test.type", "test.key");
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task UnstampedReadOnlyCall_ThrowsUnauthorizedAccessException()
    {
        var brain = fixture.NewBrain("unstamped-read");
        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");

        // F5 requirement: Read-only calls must also fail-closed when unstamped
        await Assert.ThrowsAsync<UnauthorizedAccessException>(settingsNeuron.Read);
    }

    [Fact]
    public async Task UnstampedCall_WithForgedModuleCallKey_StillThrowsUnauthorizedAccessException()
    {
        var brain = fixture.NewBrain("unstamped-forged-module");
        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");

        // Adversarial challenge: Attacker injects ModuleCallKey into Orleans RequestContext
        Orleans.Runtime.RequestContext.Set(RuntimeCallerContext.ModuleCallKey, true);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await settingsNeuron.SetProfile("Forged", "light", "gpt-4");
            });
        }
        finally
        {
            Orleans.Runtime.RequestContext.Remove(RuntimeCallerContext.ModuleCallKey);
        }
    }

    [Fact]
    public async Task StampedCall_FromForeignBrain_ThrowsUnauthorizedAccessException()
    {
        var targetBrain = fixture.NewBrain("victim-brain");
        var attackerBrain = fixture.NewBrain("attacker-brain");

        // Attacker stamps caller with attackerBrain identity
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(attackerBrain, attackerBrain, attackerBrain));
        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{targetBrain}/settings");

        // Should be rejected by PlainNeuronCallFilter.Authorize because target brain != caller brain
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await settingsNeuron.SetProfile("Intruder", "dark", "gpt-5");
        });
    }

    [Fact]
    public async Task StampedCall_ReadOnlyMethod_SucceedsForLegitimateCaller()
    {
        var brain = fixture.NewBrain("stamped-read");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));
        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");

        await settingsNeuron.SetProfile("Ada", "dark", "gpt-5");
        var state = await settingsNeuron.Read();
        Assert.NotNull(state);
        Assert.Equal("Ada", state.DisplayName);
    }
}
