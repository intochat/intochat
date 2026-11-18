// Lives in the DigitalBrain assembly (DigitalBrain.UI cannot reference it) but in the UI.Commands namespace: the names (Select) match view nodes.
namespace DigitalBrain.UI.Commands;

/// <summary>Flips a control between on and off.</summary>
[Signal("control.toggle")]
public sealed record Toggle : Signal;

/// <summary>Sets a control's checked state.</summary>
/// <param name="IsOn">True when on.</param>
[Signal("control.setChecked")]
public sealed record SetChecked(bool IsOn) : Signal;

/// <summary>Selects an item.</summary>
/// <param name="Id">The item id.</param>
[Signal("control.select")]
public sealed record Select(string Id) : Signal;

/// <summary>Expands or collapses a control.</summary>
/// <param name="IsExpanded">True when expanded.</param>
[Signal("control.expand")]
public sealed record Expand(bool IsExpanded) : Signal;

/// <summary>Submits a form's values.</summary>
/// <param name="Values">The submitted values by field name: every editable field, typed or not.</param>
[Signal("control.submit")]
public sealed record Submit(IReadOnlyDictionary<string, string> Values) : Signal;

/// <summary>Closes or dismisses a control.</summary>
[Signal("control.close")]
public sealed record Close : Signal;

/// <summary>A control's new on/off state.</summary>
/// <param name="IsOn">True when on.</param>
[Signal("control.toggled")]
public sealed record Toggled(bool IsOn) : Signal;

/// <summary>The id of the newly selected item.</summary>
/// <param name="Id">The item id.</param>
[Signal("control.selectionChanged")]
public sealed record SelectionChanged(string Id) : Signal;

/// <summary>A control's new expanded state.</summary>
/// <param name="IsExpanded">True when expanded.</param>
[Signal("control.expanded")]
public sealed record Expanded(bool IsExpanded) : Signal;

/// <summary>The values a control submitted.</summary>
/// <param name="Values">The submitted values by field name.</param>
[Signal("control.submitted")]
public sealed record Submitted(IReadOnlyDictionary<string, string> Values) : Signal;

/// <summary>A control was closed or dismissed.</summary>
[Signal("control.closed")]
public sealed record Closed : Signal;

/// <summary>Replaces a live chart's points (only the last 200 are kept).</summary>
/// <param name="Points">The new points, oldest first.</param>
[Signal("control.replace")]
public sealed record Replace(IReadOnlyList<DigitalBrain.UI.LivePoint> Points) : Signal;

/// <summary>Appends a point to a live chart; a point whose event id the chart still holds is ignored.</summary>
/// <param name="Point">The point.</param>
[Signal("control.append")]
public sealed record Append(DigitalBrain.UI.LivePoint Point) : Signal;

/// <summary>A live chart changed.</summary>
/// <param name="Version">The chart's change counter after the change.</param>
[Signal("control.chartChanged")]
public sealed record ChartChanged(int Version) : Signal;
