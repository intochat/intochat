using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Time;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class KernelTypeFacts
{
    [Fact]
    public async Task TheKernelKnowsPublicConnectionDiscoveryButNoCredentialContracts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var names = (await Types(brain, ct)).Select(type => type.Name).ToArray();
        Assert.Contains(typeof(DigitalBrain.Integrations.IConnectionRequests).FullName!, names);
        Assert.DoesNotContain(typeof(IIntegration).FullName!, names);
        Assert.DoesNotContain(typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).FullName!, names);
    }

    [Fact]
    public async Task TypesAreDiscoveredAndFoundByWordsWithoutAnEmbeddingModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<TimeModule>().WithReminders().StartAsync(ct);
        var types = await Types(brain, ct);

        var timer = types.Single(type => type.Kind == TypeKind.Neuron && type.Alias == "timer");
        Assert.Contains(timer.Shape, method => method.Contains("Start", StringComparison.Ordinal));
        Assert.Equal((typeof(DigitalBrain.Time.Timers.ITimer).FullName, "time"), (timer.Name, timer.Provider));
        Assert.Contains(types, type => type.Kind == TypeKind.Signal && type.Provider == "time" && type.Name.EndsWith("TimerTick", StringComparison.Ordinal) && type.Shape.Any(member => member.EndsWith(" TimerId", StringComparison.Ordinal)));
        Assert.Contains(types, type => type.Alias == "reminder");
        Assert.DoesNotContain(types, type => type.Alias == "test-registry-clock" || type.Alias == "test.registry-clock");
        Assert.Contains(await Stamped(brain, () => brain.SiloServices.GetRequiredService<NeuronTypeSearch>().Search("schedule a timer", ct: ct)), hit => hit.Type.Id == "timer");
    }

    [Fact]
    public async Task TheIntegrationRegistrationContractIsExcludedFromTheNeuronTypeCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var types = await Types(brain, ct);

        Assert.DoesNotContain(types, type => type.Alias == "integration.registration" || type.Name == typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName);
    }

    [Fact]
    public async Task TheWholePlatformAssemblyContributesNoNeuronTypesToTheCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var types = await Types(brain, ct);

        var platformAssembly = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        var platformContracts = platformAssembly.GetExportedTypes().Where(type => type.IsInterface).Select(type => type.FullName);
        Assert.DoesNotContain(types, type => platformContracts.Contains(type.Name));
    }

    // The kernel answers for the stamped caller's brain, as every surface asks it.
    internal static Task<TypeEntry[]> Types(ModuleBrain brain, CancellationToken ct)
        => Stamped(brain, () => brain.SiloServices.GetRequiredService<IKernel>().Types(ct));

    internal static Task<T> Stamped<T>(ModuleBrain brain, Func<Task<T>> read)
    {
        _ = brain;
        CallerContextStamper.Stamp(new()
        { PrincipalId = "owner", AccountId = "owner", BrainId = "types", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        return read();
    }

}

public sealed class RegistryFixtureModule() : Module(new ModuleId(nameof(RegistryFixtureModule)))
{
    public override System.Reflection.Assembly? Contracts => typeof(RegistryFixtureModule).Assembly;
    public override void Configure(ISiloBuilder silo) { }
}

[Alias("test.registry-clock"), Description("Schedule alarms and wake up at a requested time.")]
public interface IRegistryClock : INeuron { Task Ping(); }

[GrainType("test-registry-clock")]
public sealed class RegistryClock : Neuron, IRegistryClock
{
    public Task Ping() => Task.CompletedTask;
}
