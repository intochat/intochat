using System.Reflection;
using DigitalBrain;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

// A control neuron's [Emits] is what the generator and the showcase catalog tell clients it publishes; reflection cannot see
// what it passes to Show, so this runs every command of every control neuron and holds [Emits] to what was really published.
public sealed class ControlEmitsFacts
{
    private static readonly string CommandsNamespace = typeof(DigitalBrain.UI.Commands.Toggle).Namespace!;

    public static TheoryData<string> Neurons()
        => [.. typeof(CheckBoxNeuron).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.Namespace == typeof(CheckBoxNeuron).Namespace && IsScreen(type))
            .Select(type => type.FullName!).Order(StringComparer.Ordinal)];

    [Theory, MemberData(nameof(Neurons))]
    public async Task Emits_lists_exactly_what_its_commands_publish(string neuronName)
    {
        var neuron = typeof(CheckBoxNeuron).Assembly.GetType(neuronName)!;
        await using var control = new ControlHarness(neuron);
        foreach (var command in Commands(neuron).SelectMany(Samples))
        {
            try { await control.SendRaw(command); }
            catch (ActivationRefusedException) { /* a refused sample publishes nothing; another sample covers the command */ }
        }

        var published = control.Published().Select(signal => signal.GetType()).Distinct().ToHashSet();
        var declared = neuron.GetCustomAttributes<EmitsAttribute>().Select(emits => emits.Signal).ToHashSet();
        Assert.True(published.IsSubsetOf(declared), $"{neuron.Name} publishes {string.Join(", ", published.Except(declared).Select(t => t.Name))} without [Emits].");
        Assert.True(declared.IsSubsetOf(published), $"{neuron.Name} declares [Emits] {string.Join(", ", declared.Except(published).Select(t => t.Name))} that no command published.");
    }

    [Fact]
    public void Every_control_neuron_is_covered() => Assert.NotEmpty(Neurons());

    private static bool IsScreen(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Screen<>)) { return true; }
        }
        return false;
    }

    private static IEnumerable<Type> Commands(Type neuron) => neuron.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => method.GetCustomAttribute<DigitalBrain.Neurons.OnAttribute>() is not null)
        .Select(method => method.GetParameters())
        .Where(parameters => parameters.Length == 1 && parameters[0].ParameterType.Namespace == CommandsNamespace)
        .Select(parameters => parameters[0].ParameterType).Distinct().OrderBy(type => type.Name, StringComparer.Ordinal);

    // Each command with every value of its bool arguments, a sample string and an empty map; run twice so each value is a change.
    private static IEnumerable<Signal> Samples(Type command)
    {
        var constructor = command.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var parameters = constructor.GetParameters();
        bool[] flips = parameters.Any(p => p.ParameterType == typeof(bool)) ? [true, false, true] : [true, true];
        foreach (var flip in flips)
        {
            yield return (Signal)constructor.Invoke([.. parameters.Select(p => Sample(p.ParameterType, flip))]);
        }
    }

    private static object? Sample(Type type, bool flip) => type == typeof(bool) ? flip
        : type == typeof(string) ? "sample"
        : type == typeof(IReadOnlyDictionary<string, string>) ? new Dictionary<string, string>(StringComparer.Ordinal)
        : throw new NotSupportedException($"No sample for a command argument of type {type}.");
}
