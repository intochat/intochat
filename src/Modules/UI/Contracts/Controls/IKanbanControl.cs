using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A kanban board, whose columns and cards are its parent's props: it owns the selected card. A screen mounts it with <c>Child&lt;IKanbanControl, KanbanProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.kanban")]
[DefaultNeuronType("control.kanban")]
public interface IKanbanControl : IControl<KanbanProps>;
