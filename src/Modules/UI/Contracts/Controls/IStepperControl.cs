using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A stepper, whose steps are its parent's props: it owns the active step. A screen mounts it with <c>Child&lt;IStepperControl, StepperProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.stepper")]
[DefaultNeuronType("control.stepper")]
public interface IStepperControl : IScreen, IControl<StepperProps>;
