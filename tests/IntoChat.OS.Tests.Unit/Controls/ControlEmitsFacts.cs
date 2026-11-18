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
        Assert.True(ControlSamples.All.TryGetValue(neuron, out var sample), $"{neuron.Name} has no row in ControlSamples.");
        var missing = Commands(neuron).Where(command => !sample.Commands.Any(sent => sent.GetType() == command)).Select(command => command.Name).ToArray();
        Assert.True(missing.Length == 0, $"The {neuron.Name} sample sends no {string.Join(", ", missing)}.");

        await using var control = new ControlHarness(neuron, state: sample.State);
        var accepted = new HashSet<Type>();
        var refused = new Dictionary<Type, string>();
        foreach (var command in sample.Commands)
        {
            try
            {
                await control.SendRaw(command);
                accepted.Add(command.GetType());
            }
            catch (ActivationRefusedException refusal)
            {
                refused[command.GetType()] = refusal.Reason;
            }
        }
        var neverAccepted = refused.Where(entry => !accepted.Contains(entry.Key)).Select(entry => $"{entry.Key.Name} ({entry.Value})").ToArray();
        Assert.True(neverAccepted.Length == 0, $"Every {neuron.Name} sample of {string.Join(", ", neverAccepted)} was refused.");

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
}
