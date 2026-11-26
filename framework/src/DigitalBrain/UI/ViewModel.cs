using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>Marks a concrete <see cref="Node"/> record with its wire discriminator <paramref name="kind"/> for schema generation.</summary>
/// <param name="kind">The polymorphic <c>kind</c> discriminator written in JSON.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ViewNodeAttribute(string kind) : Attribute
{
    private static readonly FrozenDictionary<string, Type> KnownNodes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["column"] = typeof(Column),
        ["row"] = typeof(Row),
        ["grid"] = typeof(Grid),
        ["splitPane"] = typeof(SplitPane),
        ["surface"] = typeof(Surface),
        ["divider"] = typeof(Divider),
        ["spacer"] = typeof(Spacer),
        ["collection"] = typeof(Collection),
        ["heading"] = typeof(Heading),
        ["text"] = typeof(Text),
        ["markdown"] = typeof(Markdown),
        ["image"] = typeof(Image),
        ["jsonTree"] = typeof(JsonTree),
        ["badge"] = typeof(Badge),
        ["tagPill"] = typeof(TagPill),
        ["field"] = typeof(Field),
        ["intent"] = typeof(Intent),
        ["progress"] = typeof(Progress),
        ["infoBar"] = typeof(InfoBar),
        ["toast"] = typeof(Toast),
        ["card"] = typeof(Card),
        ["button"] = typeof(Button),
        ["iconButton"] = typeof(IconButton),
        ["dataTable"] = typeof(DataTable),
        ["chart"] = typeof(Chart),
        ["approvalCard"] = typeof(ApprovalCardNode),
        ["choiceCard"] = typeof(ChoiceCardNode),
        ["draftCard"] = typeof(DraftCardNode),
        ["recordCard"] = typeof(RecordCardNode),
        ["secretCard"] = typeof(SecretCardNode),
        ["jobCard"] = typeof(JobCardNode),
        ["brainScene"] = typeof(BrainSceneNode),
        ["neuronRef"] = typeof(NeuronRef),
        ["appShell"] = typeof(AppShellNode),
        ["navItem"] = typeof(NavItemNode),
        ["statCard"] = typeof(StatCardNode),
        ["webBrowser"] = typeof(WebBrowser),
        ["conversation"] = typeof(Conversation),
        ["errorLine"] = typeof(ErrorLineNode),
        ["emptyState"] = typeof(EmptyStateNode),
        ["metricTile"] = typeof(MetricTileNode),
        ["appTile"] = typeof(AppTileNode),
        ["diffCard"] = typeof(DiffCardNode),
        ["checkBoxView"] = typeof(Controls.CheckBoxView),
        ["toggleSwitchView"] = typeof(Controls.ToggleSwitchView),
        ["expanderView"] = typeof(Controls.ExpanderView),
        ["tabStripView"] = typeof(Controls.TabStripView),
        ["comboBoxView"] = typeof(Controls.ComboBoxView),
        ["formView"] = typeof(Controls.FormView),
        ["liveChartView"] = typeof(Controls.LiveChartView),
        ["chatThreadView"] = typeof(Controls.ChatThreadView),
        ["queryTableView"] = typeof(Controls.QueryTableView),
        ["listBoxView"] = typeof(Controls.ListBoxView),
        ["kanbanView"] = typeof(Controls.KanbanView),
        ["stepperView"] = typeof(Controls.StepperView),
        ["treeView"] = typeof(Controls.TreeView),
        ["sliderView"] = typeof(Controls.SliderView),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The polymorphic <c>kind</c> discriminator written in JSON.</summary>
    public string Kind { get; } = string.IsNullOrWhiteSpace(kind)
        ? throw new ArgumentException("A view node kind cannot be empty.", nameof(kind))
        : kind;

    /// <summary>Returns every concrete <see cref="Node"/> type in <c>DigitalBrain.UI</c> keyed by its <see cref="Kind"/> discriminator (AOT-safe static registry).</summary>
    public static IReadOnlyDictionary<string, Type> DiscoverAll() => KnownNodes;
}

/// <summary>A screen an app returns: a title and a tree of nodes. Renderer-neutral.</summary>
public sealed record View(string Title, Node Root)
{
    /// <summary>The value of the wire field <c>acts</c> on a view whose commands all go to the act route. Only the host that serves a
    /// screen's view writes it into the outgoing JSON; a view has no such property, so app code cannot set it.</summary>
    public const string ActRoute = "act";
}

/// <summary>A node of a view.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Column), "column")]
[JsonDerivedType(typeof(Row), "row")]
[JsonDerivedType(typeof(Grid), "grid")]
[JsonDerivedType(typeof(SplitPane), "splitPane")]
[JsonDerivedType(typeof(Surface), "surface")]
[JsonDerivedType(typeof(Divider), "divider")]
[JsonDerivedType(typeof(Spacer), "spacer")]
[JsonDerivedType(typeof(Collection), "collection")]
[JsonDerivedType(typeof(Heading), "heading")]
[JsonDerivedType(typeof(Text), "text")]
[JsonDerivedType(typeof(Markdown), "markdown")]
[JsonDerivedType(typeof(Image), "image")]
[JsonDerivedType(typeof(JsonTree), "jsonTree")]
[JsonDerivedType(typeof(Badge), "badge")]
[JsonDerivedType(typeof(TagPill), "tagPill")]
[JsonDerivedType(typeof(Field), "field")]
[JsonDerivedType(typeof(Intent), "intent")]
[JsonDerivedType(typeof(Progress), "progress")]
[JsonDerivedType(typeof(InfoBar), "infoBar")]
[JsonDerivedType(typeof(Toast), "toast")]
[JsonDerivedType(typeof(Card), "card")]
[JsonDerivedType(typeof(Button), "button")]
[JsonDerivedType(typeof(IconButton), "iconButton")]
[JsonDerivedType(typeof(DataTable), "dataTable")]
[JsonDerivedType(typeof(Chart), "chart")]
[JsonDerivedType(typeof(ApprovalCardNode), "approvalCard")]
[JsonDerivedType(typeof(ChoiceCardNode), "choiceCard")]
[JsonDerivedType(typeof(DraftCardNode), "draftCard")]
[JsonDerivedType(typeof(RecordCardNode), "recordCard")]
[JsonDerivedType(typeof(SecretCardNode), "secretCard")]
[JsonDerivedType(typeof(JobCardNode), "jobCard")]
[JsonDerivedType(typeof(BrainSceneNode), "brainScene")]
[JsonDerivedType(typeof(NeuronRef), "neuronRef")]
[JsonDerivedType(typeof(AppShellNode), "appShell")]
[JsonDerivedType(typeof(NavItemNode), "navItem")]
[JsonDerivedType(typeof(StatCardNode), "statCard")]
[JsonDerivedType(typeof(WebBrowser), "webBrowser")]
[JsonDerivedType(typeof(Conversation), "conversation")]
[JsonDerivedType(typeof(ErrorLineNode), "errorLine")]
[JsonDerivedType(typeof(EmptyStateNode), "emptyState")]
[JsonDerivedType(typeof(MetricTileNode), "metricTile")]
[JsonDerivedType(typeof(AppTileNode), "appTile")]
[JsonDerivedType(typeof(DiffCardNode), "diffCard")]
[JsonDerivedType(typeof(Controls.CheckBoxView), "checkBoxView")]
[JsonDerivedType(typeof(Controls.ToggleSwitchView), "toggleSwitchView")]
[JsonDerivedType(typeof(Controls.ExpanderView), "expanderView")]
[JsonDerivedType(typeof(Controls.TabStripView), "tabStripView")]
[JsonDerivedType(typeof(Controls.ComboBoxView), "comboBoxView")]
[JsonDerivedType(typeof(Controls.FormView), "formView")]
[JsonDerivedType(typeof(Controls.LiveChartView), "liveChartView")]
[JsonDerivedType(typeof(Controls.ChatThreadView), "chatThreadView")]
[JsonDerivedType(typeof(Controls.QueryTableView), "queryTableView")]
[JsonDerivedType(typeof(Controls.ListBoxView), "listBoxView")]
[JsonDerivedType(typeof(Controls.KanbanView), "kanbanView")]
[JsonDerivedType(typeof(Controls.StepperView), "stepperView")]
[JsonDerivedType(typeof(Controls.TreeView), "treeView")]
[JsonDerivedType(typeof(Controls.SliderView), "sliderView")]
public abstract record Node;
