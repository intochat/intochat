using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Core;

namespace IntoChat.OS.Architecture.Tests.Unit;

/// <summary>
/// DDD vertical-slice fitness tests for <c>IntoChat.OS.Modules.UI</c>:
/// 1. Contracts are co-located in the same folder as their neuron (no separate <c>Contracts/</c> tree).
/// 2. Every stateful UI control neuron inherits <see cref="UiNeuronBase{TState}"/>.
/// </summary>
public sealed class UiVerticalSliceFacts
{
    [Fact]
    public void UiModuleHasNoSeparateContractsFolder()
    {
        var uiDir = Repository.Projects["IntoChat.OS.Modules.UI"].Directory;
        var separateContractsDir = Path.Combine(uiDir, "Contracts");
        Assert.False(
            Directory.Exists(separateContractsDir),
            $"UI contracts must be co-located inside each neuron folder under IntoChat.OS.Modules.UI, not in '{separateContractsDir}'.");
    }

    [Fact]
    public void EveryUiControlNeuronInheritsUiNeuronBase()
    {
        var uiAssembly = typeof(UiModule).Assembly;
        var violations = new List<string>();

        foreach (var type in uiAssembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass) { continue; }
            if (!type.Name.EndsWith("Neuron", StringComparison.Ordinal)) { continue; }
            if (!InheritsOpenGeneric(type, typeof(UiNeuronBase<>)))
            {
                violations.Add(type.FullName ?? type.Name);
            }
        }

        Assert.Empty(violations);
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
