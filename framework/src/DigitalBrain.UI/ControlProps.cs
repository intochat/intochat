using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>A control neuron whose props are <typeparamref name="TProps"/>: it ties <c>Slot&lt;TChild, TProps&gt;(key, props)</c> to the child's own props record.</summary>
/// <typeparam name="TProps">The props record the parent writes on the control's slot.</typeparam>
#pragma warning disable CA1040 // A marker: the constraint is the whole point.
public interface IControl<TProps> where TProps : ControlProps;
#pragma warning restore CA1040

/// <summary>
/// A control's props: the setup its parent decides, written on the slot node in the parent's view (<c>Slot&lt;TChild&gt;(key, props)</c>)
/// and re-rendered with the parent. The child neuron stores only what the user changes. A command routed through the parent to a slot
/// whose props are disabled is refused with <see cref="Disabled"/>.
/// </summary>
/// <param name="IsEnabled">False draws the control disabled, and the parent refuses every command routed to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the label or header, then the slot key.</param>
public abstract record ControlProps(bool IsEnabled = true, string? AutomationName = null)
{
    /// <summary>The refusal reason of a command to a slot whose props are disabled.</summary>
    public const string Disabled = "control.disabled";
}

/// <summary>The props of a check box: what its parent decides.</summary>
/// <param name="Label">The text beside the box.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the label.</param>
/// <param name="IsOn">Set when the parent owns the value (a controlled control): drawn instead of the child's state, and a command
/// only signals Toggled with the requested value; the child stores nothing. Null leaves the value to the child.</param>
public sealed record CheckBoxProps(string Label, bool IsEnabled = true, string? AutomationName = null, bool? IsOn = null) : ControlProps(IsEnabled, AutomationName);

/// <summary>The props of a toggle switch: what its parent decides.</summary>
/// <param name="Label">The header beside the switch.</param>
/// <param name="OnContent">The text shown while on; null shows none.</param>
/// <param name="OffContent">The text shown while off; null shows none.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the label.</param>
/// <param name="IsOn">Set when the parent owns the value (a controlled control): drawn instead of the child's state, and a command
/// only signals Toggled with the requested value; the child stores nothing. Null leaves the value to the child.</param>
public sealed record ToggleSwitchProps(string Label, string? OnContent = null, string? OffContent = null, bool IsEnabled = true, string? AutomationName = null, bool? IsOn = null)
    : ControlProps(IsEnabled, AutomationName);

/// <summary>The props of an expander: what its parent decides.</summary>
/// <param name="Header">The always-visible header.</param>
/// <param name="Children">The content shown while expanded (data nodes).</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the header.</param>
public sealed record ExpanderProps(string Header, IReadOnlyList<Node> Children, bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName);

/// <summary>
/// Props that check a command routed to their slot. The parent's Send asks them about every command before the child is reached, because
/// what the check needs (the items, the mode) is the parent's: the child stores only what the user changes.
/// </summary>
public interface ICommandGate
{
    /// <summary>The refusal reason of <paramref name="signal"/> with <paramref name="args"/>, or null to pass it to the child.</summary>
    /// <param name="signal">The command's signal alias.</param>
    /// <param name="args">The command's arguments as wire JSON, if any.</param>
    string? RefusalOf(string signal, JsonElement? args);
}

/// <summary>The refusal reasons of a selection routed to a slot.</summary>
public static class SelectionRefusals
{
    /// <summary>The alias of the select command (<c>DigitalBrain.UI.Commands.Select</c>, which this assembly cannot reference).</summary>
    public const string SelectSignal = "control.select";

    // The refusal of a select command whose id `refusal` judges; other signals pass.
    internal static string? OfSelect(string signal, JsonElement? args, Func<string?, string?> refusal)
        => signal != SelectSignal ? null
            : refusal(args is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null);

    /// <summary>The id names no item of the slot's props.</summary>
    public const string UnknownItem = "control.unknownItem";

    /// <summary>The slot's props allow no selection.</summary>
    public const string SelectionOff = "control.selectionOff";
}

/// <summary>An item of a tab strip.</summary>
/// <param name="Id">The id a selection names.</param>
/// <param name="Label">The tab's text.</param>
/// <param name="Icon">An optional icon name.</param>
public sealed record TabStripItem(string Id, string Label, string? Icon = null);

/// <summary>The props of a tab strip: what its parent decides.</summary>
/// <param name="Items">The tabs, in order.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the slot key.</param>
/// <param name="SelectedId">Set when the parent owns the selection (a controlled control): drawn instead of the child's state, and a
/// Select only signals SelectionChanged; the child stores nothing. Null leaves the selection to the child.</param>
public sealed record TabStripProps(IReadOnlyList<TabStripItem> Items, bool IsEnabled = true, string? AutomationName = null, string? SelectedId = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args)
        => SelectionRefusals.OfSelect(signal, args, id => Items.Any(item => item.Id == id) ? null : SelectionRefusals.UnknownItem);

    /// <inheritdoc />
    public bool Equals(TabStripProps? other) => other is not null && base.Equals(other) && SelectedId == other.SelectedId && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SequenceHash.Of(Items));
}

/// <summary>An option of a combo box.</summary>
/// <param name="Id">The id a selection names.</param>
/// <param name="Label">The option's text.</param>
public sealed record ComboBoxOption(string Id, string Label);

/// <summary>The props of a combo box: what its parent decides.</summary>
/// <param name="Header">The text above the box.</param>
/// <param name="Options">The options, in order.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the header.</param>
/// <param name="SelectedId">Set when the parent owns the selection (a controlled control): drawn instead of the child's state, and a
/// Select only signals SelectionChanged; the child stores nothing. Null leaves the selection to the child.</param>
public sealed record ComboBoxProps(string Header, IReadOnlyList<ComboBoxOption> Options, bool IsEnabled = true, string? AutomationName = null, string? SelectedId = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args)
        => SelectionRefusals.OfSelect(signal, args, id => Options.Any(option => option.Id == id) ? null : SelectionRefusals.UnknownItem);

    /// <inheritdoc />
    public bool Equals(ComboBoxProps? other) => other is not null && base.Equals(other) && Header == other.Header && SelectedId == other.SelectedId && Options.SequenceEqual(other.Options);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Header, SequenceHash.Of(Options));
}

/// <summary>An item of a list box.</summary>
/// <param name="Id">The id a selection names.</param>
/// <param name="Title">The item's main line.</param>
/// <param name="Subtitle">An optional muted second line.</param>
/// <param name="Group">The group the item belongs to: a header is drawn where the group changes from the item before; null draws none.</param>
public sealed record ListBoxItem(string Id, string Title, string? Subtitle = null, string? Group = null);

/// <summary>Whether a list box lets a person select an item.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ListSelectionMode>))]
public enum ListSelectionMode
{
    /// <summary>One item can be selected.</summary>
#pragma warning disable CA1720 // WinUI's name for the mode; the wire value is "Single".
    Single,
#pragma warning restore CA1720

    /// <summary>Nothing can be selected: the list only shows its items.</summary>
    None,
}

/// <summary>The props of a list box: what its parent decides.</summary>
/// <param name="Items">The items, in order.</param>
/// <param name="SelectionMode">Single lets one item be selected; None refuses every selection (control.selectionOff).</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the slot key.</param>
/// <param name="SelectedId">Set when the parent owns the selection (a controlled control): drawn instead of the child's state, and a
/// Select only signals SelectionChanged; the child stores nothing. Null leaves the selection to the child.</param>
public sealed record ListBoxProps(IReadOnlyList<ListBoxItem> Items, ListSelectionMode SelectionMode = ListSelectionMode.Single, bool IsEnabled = true, string? AutomationName = null, string? SelectedId = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args) => SelectionRefusals.OfSelect(signal, args, id => SelectionMode == ListSelectionMode.None
        ? SelectionRefusals.SelectionOff
        : Items.Any(item => item.Id == id) ? null : SelectionRefusals.UnknownItem);

    /// <inheritdoc />
    public bool Equals(ListBoxProps? other) => other is not null && base.Equals(other) && SelectionMode == other.SelectionMode && SelectedId == other.SelectedId && Items.SequenceEqual(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SelectionMode, SequenceHash.Of(Items));
}

/// <summary>What a form field holds and how the client draws it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FormFieldKind>))]
public enum FormFieldKind
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>Several lines of text.</summary>
    Multiline,

    /// <summary>A secret. Not supported yet: a form whose props hold one refuses every submit (control.secretUnsupported) and the
    /// client does not draw it, because a submit is a journaled command and signal. A vault-backed path is owed.</summary>
    Secret,

    /// <summary>A number (invariant culture); an empty value is allowed.</summary>
    Number,
}

/// <summary>A field of a form.</summary>
/// <param name="Key">The key its value is submitted under.</param>
/// <param name="Label">The text above the field.</param>
/// <param name="Kind">What it holds; <see cref="FormFieldKind.Secret"/> is not supported yet.</param>
/// <param name="Value">Set when the parent owns the value (a controlled field): drawn until the person types, and a form with any such
/// field stores nothing on submit, it only signals Submitted. Null draws the form's last submitted value.</param>
/// <param name="Placeholder">The hint shown while empty.</param>
/// <param name="MaxLength">The longest value the parent accepts; a longer submit is refused (control.tooLong).</param>
/// <param name="IsReadOnly">True draws the value without editing; a submit naming it is refused (control.readOnlyField).</param>
public sealed record FormInput(
    string Key,
    string Label,
    FormFieldKind Kind = FormFieldKind.Text,
    string? Value = null,
    string? Placeholder = null,
    int? MaxLength = null,
    bool IsReadOnly = false);

/// <summary>A submitted value a form keeps, as it draws it.</summary>
/// <param name="Key">The field's key.</param>
/// <param name="Value">The last submitted value.</param>
public sealed record FormValue(string Key, string Value);

/// <summary>The refusal reasons of a submit routed to a form slot.</summary>
public static class FormRefusals
{
    /// <summary>The alias of the submit command (<c>DigitalBrain.UI.Commands.Submit</c>, which this assembly cannot reference).</summary>
    public const string SubmitSignal = "control.submit";

    /// <summary>A submitted key names no field of the slot's props.</summary>
    public const string UnknownField = "control.unknownField";

    /// <summary>A submitted key names a read-only field.</summary>
    public const string ReadOnlyField = "control.readOnlyField";

    /// <summary>A submitted value is longer than its field's MaxLength.</summary>
    public const string TooLong = "control.tooLong";

    /// <summary>A number field's value is not a number.</summary>
    public const string NotANumber = "control.notANumber";

    /// <summary>The form's props hold a secret field, which forms do not support yet.</summary>
    public const string SecretUnsupported = "control.secretUnsupported";

    /// <summary>The submit carries no values object.</summary>
    public const string NoValues = "control.noValues";
}

/// <summary>The props of a form: what its parent decides. The client keeps typed text until Submit, which carries every editable value at once.</summary>
/// <param name="Fields">The fields, in order.</param>
/// <param name="SubmitLabel">The submit button's text.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the slot key.</param>
public sealed record FormProps(IReadOnlyList<FormInput> Fields, string SubmitLabel = "Submit", bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args)
    {
        if (signal != FormRefusals.SubmitSignal) { return null; }
        if (args is not { ValueKind: JsonValueKind.Object } json || !json.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            return FormRefusals.NoValues;
        }
        if (Fields.Any(field => field.Kind == FormFieldKind.Secret)) { return FormRefusals.SecretUnsupported; }
        foreach (var entry in values.EnumerateObject())
        {
            if (Fields.FirstOrDefault(field => field.Key == entry.Name) is not { } field) { return FormRefusals.UnknownField; }
            if (field.IsReadOnly) { return FormRefusals.ReadOnlyField; }
            var value = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() ?? "" : entry.Value.GetRawText();
            if (field.MaxLength is { } max && value.Length > max) { return FormRefusals.TooLong; }
            if (field.Kind == FormFieldKind.Number && value.Length > 0
                && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return FormRefusals.NotANumber;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public bool Equals(FormProps? other) => other is not null && base.Equals(other) && SubmitLabel == other.SubmitLabel && Fields.SequenceEqual(other.Fields);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), SubmitLabel, SequenceHash.Of(Fields));
}

/// <summary>The refusal reasons of a command routed to a live chart slot.</summary>
public static class ChartRefusals
{
    /// <summary>The alias of the replace command (<c>DigitalBrain.UI.Commands.Replace</c>, which this assembly cannot reference).</summary>
    public const string ReplaceSignal = "control.replace";

    /// <summary>The alias of the append command (<c>DigitalBrain.UI.Commands.Append</c>, which this assembly cannot reference).</summary>
    public const string AppendSignal = "control.append";

    /// <summary>The slot's props name a chart kind the chart cannot draw.</summary>
    public const string UnknownKind = "control.unknownKind";

    /// <summary>The chart kinds: bar, line, area, pie.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["bar", "line", "area", "pie"];
}

/// <summary>The props of a live chart: what its parent decides. The chart owns the points.</summary>
/// <param name="Title">The chart's title.</param>
/// <param name="Kind">How it draws: bar, line, area or pie. Any other kind is refused (control.unknownKind) and the client draws an error.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the title.</param>
public sealed record LiveChartProps(string Title, string Kind = "bar", bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <summary>Whether <see cref="Kind"/> is one the chart can draw.</summary>
    [JsonIgnore]
    public bool KindIsKnown => ChartRefusals.Kinds.Contains(Kind, StringComparer.Ordinal);

    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args)
        => signal is ChartRefusals.ReplaceSignal or ChartRefusals.AppendSignal && !KindIsKnown ? ChartRefusals.UnknownKind : null;
}

/// <summary>A point of a live chart. The event id is how a feed's repeated delivery is recognised and ignored.</summary>
/// <param name="EventId">The id of the event the point came from; blank is never deduplicated.</param>
/// <param name="Label">The point's label.</param>
/// <param name="Value">The point's value.</param>
public sealed record LivePoint(string EventId, string Label, double Value);

/// <summary>The refusal reasons of a command routed to a query table slot.</summary>
public static class TableRefusals
{
    /// <summary>The alias of the sort command (<c>DigitalBrain.UI.Commands.Sort</c>, which this assembly cannot reference).</summary>
    public const string SortSignal = "control.sort";

    /// <summary>The alias of the page command (<c>DigitalBrain.UI.Commands.Page</c>, which this assembly cannot reference).</summary>
    public const string PageSignal = "control.page";

    /// <summary>A sort names no column of the slot's props.</summary>
    public const string UnknownColumn = "control.unknownColumn";

    /// <summary>A page number below 1.</summary>
    public const string PageOutOfRange = "control.pageOutOfRange";
}

/// <summary>A column of a query table: the id its rows are keyed by and the title drawn above it.</summary>
/// <param name="Id">The column id the source keys row values by.</param>
/// <param name="Title">The header text.</param>
public sealed record QueryTableColumn(string Id, string Title);

/// <summary>
/// The props of a query table: what its parent decides. The table owns only its query (filter, sort, page); the rows are read from the
/// source at <see cref="SourceAddress"/> and never stored.
/// </summary>
/// <param name="Title">The table's title.</param>
/// <param name="Columns">The columns to show, in order; a sort naming any other column is refused (control.unknownColumn).</param>
/// <param name="SourceAddress">The address of the neuron the rows are read from (a table source of the same brain).</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the title.</param>
public sealed record QueryTableProps(string Title, IReadOnlyList<QueryTableColumn> Columns, string SourceAddress, bool IsEnabled = true, string? AutomationName = null)
    : ControlProps(IsEnabled, AutomationName), ICommandGate
{
    /// <inheritdoc />
    public string? RefusalOf(string signal, JsonElement? args)
    {
        var json = args is { ValueKind: JsonValueKind.Object } value ? value : (JsonElement?)null;
        return signal switch
        {
            TableRefusals.SortSignal => json is { } sort && sort.TryGetProperty("column", out var column) && column.ValueKind == JsonValueKind.String
                && Columns.Any(known => known.Id == column.GetString()) ? null : TableRefusals.UnknownColumn,
            TableRefusals.PageSignal => json is { } page && page.TryGetProperty("number", out var number) && number.TryGetInt32(out var n) && n >= 1
                ? null : TableRefusals.PageOutOfRange,
            _ => null,
        };
    }

    /// <inheritdoc />
    public bool Equals(QueryTableProps? other) => other is not null && base.Equals(other) && Title == other.Title && SourceAddress == other.SourceAddress && Columns.SequenceEqual(other.Columns);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Title, SourceAddress, SequenceHash.Of(Columns));
}

/// <summary>What a query table asks its source for: one page of the rows matching a filter, in an order.</summary>
/// <param name="Filter">The filter text; empty matches every row.</param>
/// <param name="SortBy">The column id to order by; null keeps the source's order.</param>
/// <param name="Descending">True orders largest first.</param>
/// <param name="Page">The page number, from 1.</param>
/// <param name="PageSize">The rows per page.</param>
public sealed record TableQuery(string Filter, string? SortBy, bool Descending, int Page, int PageSize = 50);

/// <summary>One page of a table source's rows and how many rows matched in all.</summary>
/// <param name="Rows">The page's rows: each maps a column id to its value.</param>
/// <param name="Total">How many rows matched the filter across every page.</param>
public sealed record TablePage(IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, int Total)
{
    /// <inheritdoc />
    public bool Equals(TablePage? other) => other is not null && Total == other.Total && Rows.Count == other.Rows.Count
        && Rows.Zip(other.Rows).All(pair => pair.First.Count == pair.Second.Count && pair.First.All(cell => pair.Second.TryGetValue(cell.Key, out var v) && v == cell.Value));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Rows.Count, Total);
}

/// <summary>One model choice shown in a chat composer's model picker.</summary>
/// <param name="Id">The canonical model id (e.g. "qwen3.5:4b", "gpt-5.4").</param>
/// <param name="Label">The clean display name for the person.</param>
/// <param name="Section">"local" or "cloud".</param>
/// <param name="Icon">The allowlisted brand icon key (e.g. "ollama"); null shows none.</param>
/// <param name="Abilities">Ability tags ("tools", "vision", "voice", "reasoning"); null when none.</param>
/// <param name="Available">False when the model cannot currently run (missing key or not installed).</param>
/// <param name="Note">Why an unavailable model cannot run; null when available.</param>
/// <param name="Progress">Optional download/install progress in [0, 1]; null when not installing.</param>
public sealed record ModelChoice(
    string Id,
    string Label,
    string Section,
    string? Icon = null,
    IReadOnlyList<string>? Abilities = null,
    bool Available = true,
    string? Note = null,
    double? Progress = null)
{
    /// <inheritdoc />
    public bool Equals(ModelChoice? other) => other is not null
        && Id == other.Id
        && Label == other.Label
        && Section == other.Section
        && Icon == other.Icon
        && Available == other.Available
        && Note == other.Note
        && Progress == other.Progress
        && SequenceEquals(Abilities, other.Abilities);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Id, Label, Section, Icon, Available, Note, Progress,
        Abilities is null ? 0 : SequenceHash.Of(Abilities));

    private static bool SequenceEquals(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));
}

/// <summary>The props of a chat thread: what its parent decides. The thread owns the messages.</summary>
/// <param name="Placeholder">The composer's placeholder.</param>
/// <param name="CanSend">False draws the composer disabled (the parent is busy or paused).</param>
/// <param name="ActiveModelLabel">The model the next message goes to, as the user reads it; null shows none.</param>
/// <param name="ActiveProviderIcon">The icon key of that model's brand; null shows none.</param>
/// <param name="Dictation">True offers dictation in the composer.</param>
/// <param name="Status">ready, working, paused or failed.</param>
/// <param name="Working">True draws the thread's stop button.</param>
/// <param name="EmptyText">The text drawn while the thread holds no message; null draws nothing.</param>
/// <param name="Error">An error no message carries, drawn under the messages; null draws none.</param>
/// <param name="IsEnabled">False draws it disabled; its parent refuses commands to it.</param>
/// <param name="AutomationName">The accessible name; null falls back to the slot key.</param>
/// <param name="Models">The choices offered by the composer model picker; null hides the picker list.</param>
/// <param name="ActiveModelId">The canonical id of the active model; null when unset.</param>
/// <param name="Conversation">The conversation neuron key (e.g. "conversations/c-...") to watch for streaming deltas; null disables delta watching.</param>
/// <param name="ReadAloud">True draws a read-aloud action on replies.</param>
/// <param name="TalkConversation">The conversation voice mode talks to; null hides the voice-mode button.</param>
public sealed record ChatThreadProps(string Placeholder = "Message", bool CanSend = true, string? ActiveModelLabel = null, string? ActiveProviderIcon = null,
    bool Dictation = false, string Status = "ready", bool Working = false, string? EmptyText = null, ChatThreadError? Error = null,
    bool IsEnabled = true, string? AutomationName = null, IReadOnlyList<ModelChoice>? Models = null, string? ActiveModelId = null,
    string? Conversation = null, bool ReadAloud = false, string? TalkConversation = null)
    : ControlProps(IsEnabled, AutomationName)
{
    /// <summary>The reason a failed message draws when it carries none.</summary>
    public const string NoAnswer = "The model did not answer.";

    /// <summary>The most messages a thread draws; older ones stay held but are not drawn.</summary>
    public const int MaxDrawnMessages = 100;

    /// <summary>The most characters of message text a thread draws in all.</summary>
    public const int MaxDrawnChars = 1_000_000;

    /// <summary>The key of the thread's composer node.</summary>
    public const string ComposerKey = "composer";

    /// <summary>The key of the composer's per-conversation model picker target.</summary>
    public const string SelectModelKey = "composer-model";

    /// <summary>The key of the composer's pin-default model picker target.</summary>
    public const string PinModelKey = "composer-model-default";

    /// <summary>The key of the thread's stop button.</summary>
    public const string StopKey = "stop";

    /// <summary>The key of the error line's retry button.</summary>
    public const string ErrorRetryKey = "error-action";

    /// <summary>The key of a message's retry button.</summary>
    public static string RetryKeyOf(string id) => $"retry:{id}";

    /// <summary>The key of a completed reply's regenerate button.</summary>
    public static string RegenerateKeyOf(string id) => $"regenerate:{id}";

    /// <inheritdoc />
    public bool Equals(ChatThreadProps? other) => other is not null
        && IsEnabled == other.IsEnabled
        && AutomationName == other.AutomationName
        && Placeholder == other.Placeholder
        && CanSend == other.CanSend
        && ActiveModelLabel == other.ActiveModelLabel
        && ActiveProviderIcon == other.ActiveProviderIcon
        && Dictation == other.Dictation
        && Status == other.Status
        && Working == other.Working
        && EmptyText == other.EmptyText
        && Equals(Error, other.Error)
        && ActiveModelId == other.ActiveModelId
        && Conversation == other.Conversation
        && ReadAloud == other.ReadAloud
        && TalkConversation == other.TalkConversation
        && SequenceEquals(Models, other.Models);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsEnabled);
        hash.Add(AutomationName);
        hash.Add(Placeholder);
        hash.Add(CanSend);
        hash.Add(ActiveModelLabel);
        hash.Add(ActiveProviderIcon);
        hash.Add(Dictation);
        hash.Add(Status);
        hash.Add(Working);
        hash.Add(EmptyText);
        hash.Add(Error);
        hash.Add(ActiveModelId);
        hash.Add(Conversation);
        hash.Add(ReadAloud);
        hash.Add(TalkConversation);
        if (Models is not null) { hash.Add(SequenceHash.Of(Models)); }
        return hash.ToHashCode();
    }

    private static bool SequenceEquals(IReadOnlyList<ModelChoice>? a, IReadOnlyList<ModelChoice>? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));
}

// Hashes a list's items in order with the same comparer SequenceEqual uses, so two lists of one length with different items hash apart.
internal static class SequenceHash
{
    public static int Of<T>(IEnumerable<T> items)
    {
        var hash = new HashCode();
        foreach (var item in items) { hash.Add(item); }
        return hash.ToHashCode();
    }
}
