using System.Reflection;
using DigitalBrain.Neurons;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Product invariant (Wave C and beyond): server-side UI widget Orleans neurons (control.*) and their contracts
// in IntoChat.OS.Modules.UI and DigitalBrain.UI are a core IntoChat feature so both humans and Ino inspect,
// bind, and interact with UI controls uniformly as Neurons. None of them may ever be deleted, stripped, or unregistered.
public sealed class UiWidgetNeuronPreservationFacts
{
    private static readonly (string NeuronTypeName, string ContractTypeName, string ControlKind, string FrameworkPropsTypeName)[] RequiredUiWidgets =
    [
        ("ChatThreadNeuron", "IChatThreadControl", "control.chatThread", "ChatThreadProps"),
        ("CheckBoxNeuron", "ICheckBoxControl", "control.checkBox", "CheckBoxProps"),
        ("ComboBoxNeuron", "IComboBoxControl", "control.comboBox", "ComboBoxProps"),
        ("ExpanderNeuron", "IExpanderControl", "control.expander", "ExpanderProps"),
        ("FormNeuron", "IFormControl", "control.form", "FormProps"),
        ("KanbanNeuron", "IKanbanControl", "control.kanban", "KanbanProps"),
        ("ListBoxNeuron", "IListBoxControl", "control.listBox", "ListBoxProps"),
        ("LiveChartNeuron", "ILiveChartControl", "control.liveChart", "LiveChartProps"),
        ("QueryTableNeuron", "IQueryTableControl", "control.queryTable", "QueryTableProps"),
        ("SliderNeuron", "ISliderControl", "control.slider", "SliderProps"),
        ("StepperNeuron", "IStepperControl", "control.stepper", "StepperProps"),
        ("TabStripNeuron", "ITabStripControl", "control.tabStrip", "TabStripProps"),
        ("ToggleSwitchNeuron", "IToggleSwitchControl", "control.toggleSwitch", "ToggleSwitchProps"),
        ("TreeNeuron", "ITreeControl", "control.tree", "TreeProps"),
    ];

    [Fact]
    public void AllUiWidgetNeuronsAndContractsRemainPresentAndRegistered()
    {
        var uiAssembly = typeof(UiModule).Assembly;
        var uiTypes = uiAssembly.GetTypes()
            .Where(t => !t.IsNested && t.Namespace == typeof(ICheckBoxControl).Namespace)
            .ToDictionary(t => t.Name, StringComparer.Ordinal);
        var digitalBrainUiAssembly = typeof(DigitalBrain.UI.View).Assembly;
        var frameworkControls = digitalBrainUiAssembly.GetTypes()
            .Where(t => !t.IsNested && t.Namespace == "DigitalBrain.UI")
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.Contains(OperatingSystemModules.Modules, module => module.Type == typeof(UiModule));

        foreach (var (neuronName, contractName, controlKind, frameworkPropsName) in RequiredUiWidgets)
        {
            Assert.True(uiTypes.TryGetValue(contractName, out var contractType), $"Missing UI widget contract '{contractName}' in {uiAssembly.GetName().Name}.");
            Assert.True(contractType!.IsInterface && contractType.IsPublic, $"UI widget contract '{contractName}' must remain a public interface.");

            Assert.True(uiTypes.TryGetValue(neuronName, out var neuronType), $"Missing UI widget neuron '{neuronName}' in {uiAssembly.GetName().Name}.");
            Assert.True(contractType.IsAssignableFrom(neuronType), $"UI widget neuron '{neuronName}' must implement '{contractName}'.");

            var neuronAttr = neuronType!.GetCustomAttribute<NeuronAttribute>();
            Assert.NotNull(neuronAttr);
            Assert.Equal(controlKind, neuronAttr.Type);

            Assert.True(frameworkControls.ContainsKey(frameworkPropsName), $"Missing DigitalBrain.UI control props type '{frameworkPropsName}' for '{controlKind}'.");
        }
    }
}
