using System.Reflection;
using Xunit;
using DigitalBrain;

namespace DigitalBrain.Testing;

// The floor of a module's Unit tier (TEST-01, 06-09): its DigitalBrain module registers on TestOS, every [Neuron] class under its
// declared type (unique, scheduling markers valid), and every os contract a class implements binds a TestOS proxy, so the call
// shape (JSON arguments and results, one Task per member) holds without Orleans. A module subclasses it once in its
// <Module>.Tests.Unit; behaviour facts sit beside it (BothRuntimesFixture).
public abstract class ModuleOnTestOSFacts<TModule> where TModule : DigitalBrain.Module
{
    private static readonly MethodInfo GetProxy = typeof(TestOS).GetMethod(nameof(TestOS.Get))!;

    [Fact]
    public async Task EveryNeuronClassRegistersAndEveryContractBindsOnTestOS()
    {
        var assembly = typeof(TModule).Assembly;
        var neurons = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(Neuron).IsAssignableFrom(type) && type.IsDefined(typeof(NeuronAttribute), false))
            .ToArray();
        Assert.NotEmpty(neurons);

        await using var os = TestOS.Local();
        os.Register(assembly);
        var brain = new BrainId("unit");
        foreach (var neuron in neurons)
        {
            var id = new NeuronId(brain, neuron.GetCustomAttribute<NeuronAttribute>(false)!.Type, "probe");
            foreach (var contract in neuron.GetInterfaces().Where(Contract))
            {
                var proxy = GetProxy.MakeGenericMethod(contract).Invoke(os, [id]);
                Assert.IsAssignableFrom(contract, proxy);
            }
            Assert.False(os.IsActive(id));
        }
    }

    // An os contract: a public interface of an IntoChat assembly whose members all return tasks (side-interfaces such as
    // IAsyncDisposable and the runtime's own SPI are not called through a proxy).
    private static bool Contract(Type type)
        => type is { IsInterface: true, IsPublic: true, IsGenericTypeDefinition: false }
            && type.Assembly.GetName().Name!.StartsWith("IntoChat.", StringComparison.Ordinal)
            && type.GetMethods().Length > 0
            && type.GetMethods().All(method => typeof(Task).IsAssignableFrom(method.ReturnType) || method.ReturnType.Name.StartsWith("IAsyncEnumerable", StringComparison.Ordinal));
}
