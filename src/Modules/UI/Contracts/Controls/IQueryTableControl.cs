using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A query table: it owns the query and reads one page from its source. A screen mounts it with <c>Child&lt;IQueryTableControl, QueryTableProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.queryTable")]
[DefaultNeuronType("control.queryTable")]
public interface IQueryTableControl : IScreen, IControl<QueryTableProps>;
