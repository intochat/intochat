using System.Reflection;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Modules.UI.Legacy;

namespace IntoChat.OS.Architecture.Tests.Unit;

/// <summary>
/// Fitness facts for <c>IntoChat.OS.Modules.UI</c>, the one home of controls:
/// 1. The control contracts (<c>control.*</c>) are in <c>IntoChat.OS.Modules.UI.Contracts</c>, one per control neuron, so apps,
///    components and sandboxed apps mount a control without referencing the module.
/// 2. Every control neuron (<c>IntoChat.OS.Modules.UI.Controls</c>) derives from <see cref="ControlNeuron{TState, TProps}"/> and
///    implements the contract that names its neuron type and props.
/// 3. The module has no other neuron than the read-only legacy <c>ui.chat</c> reader (<see cref="ILegacyChatThread"/>).
/// </summary>
public sealed class UiVerticalSliceFacts
{
    private static readonly Assembly ContractsAssembly = typeof(ICheckBoxControl).Assembly;

    [Fact]
    public void TheUiContractsAssemblyHoldsOnlyControlContracts()
    {
        Assert.Equal("IntoChat.OS.Modules.UI", ContractsAssembly.GetName().Name);
        var controlContracts = ContractsAssembly.GetExportedTypes()
            .Where(type => type.IsInterface && type.Namespace == typeof(ICheckBoxControl).Namespace)
            .ToArray();
        Assert.NotEmpty(controlContracts);
        var problems = controlContracts
            .Where(type => PropsOfContract(type) is null
                || type.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type.StartsWith("control.", StringComparison.Ordinal) != true)
            .Select(type => type.FullName)
            .ToArray();
        Assert.True(problems.Length == 0, "Not a control contract ([DefaultNeuronType(\"control.…\")] interface over IControl<TProps>): " + string.Join(", ", problems));
    }

    [Fact]
    public void EveryControlNeuronImplementsTheContractOfItsTypeAndProps()
    {
        var neurons = ControlNeurons();
        Assert.NotEmpty(neurons);
        var contracts = ContractsAssembly.GetExportedTypes()
            .Where(type => type.IsInterface && type.Namespace == typeof(ICheckBoxControl).Namespace)
            .ToArray();
        var problems = new List<string>();
        foreach (var neuron in neurons)
        {
            var typeId = neuron.GetCustomAttribute<NeuronAttribute>()!.Type;
            var contract = contracts.SingleOrDefault(candidate => candidate.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type == typeId);
            if (contract is null) { problems.Add($"{neuron.Name}: no contract names {typeId}"); continue; }
            if (!contract.IsAssignableFrom(neuron)) { problems.Add($"{neuron.Name} does not implement {contract.Name}"); }
            if (PropsOfContract(contract) != PropsOfNeuron(neuron)) { problems.Add($"{contract.Name} and {neuron.Name} name different props"); }
        }
        var mounted = neurons.Select(neuron => neuron.GetCustomAttribute<NeuronAttribute>()!.Type).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(contracts.Select(contract => contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)!.Type)
            .Where(typeId => !mounted.Contains(typeId)).Select(typeId => $"{typeId}: a contract no control neuron implements"));
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void TheOnlyOtherNeuronIsTheLegacyChatReader()
    {
        var others = typeof(UiModule).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.GetCustomAttribute<NeuronAttribute>() is not null
                && type.Namespace != typeof(ControlNeuron<,>).Namespace)
            .ToArray();
        var reader = Assert.Single(others);
        Assert.Equal("ui.chat", reader.GetCustomAttribute<NeuronAttribute>()!.Type);
        Assert.True(typeof(ILegacyChatThread).IsAssignableFrom(reader), $"{reader.FullName} is not the legacy chat reader.");
    }

    private static Type[] ControlNeurons() => [.. typeof(UiModule).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && type.Namespace == typeof(ControlNeuron<,>).Namespace
            && InheritsOpenGeneric(type, typeof(ControlNeuron<,>)))];

    private static Type? PropsOfContract(Type contract) => contract.GetInterfaces()
        .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IControl<>))
        .Select(candidate => candidate.GetGenericArguments()[0]).SingleOrDefault();

    private static Type? PropsOfNeuron(Type neuron)
    {
        for (var current = neuron.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ControlNeuron<,>)) { return current.GetGenericArguments()[1]; }
        }
        return null;
    }

    private static bool InheritsOpenGeneric(Type type, Type openGeneric)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGeneric)
            {
                return true;
            }
        }

        return false;
    }
}
