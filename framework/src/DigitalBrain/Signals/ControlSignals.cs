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

/// <summary>Orders a query table by a column: the column it is already ordered by flips direction, another orders it ascending. Back to page 1.</summary>
/// <param name="Column">The column id; one the slot's props do not show is refused (control.unknownColumn).</param>
[Signal("control.sort")]
public sealed record Sort(string Column) : Signal;

/// <summary>Filters a query table's rows by text. Back to page 1.</summary>
/// <param name="Text">The filter text; empty matches every row.</param>
[Signal("control.filter")]
public sealed record Filter(string Text) : Signal;

/// <summary>Shows a page of a query table; the current page reads it again.</summary>
/// <param name="Number">The page number, from 1; below 1 is refused (control.pageOutOfRange).</param>
[Signal("control.page")]
public sealed record Page(int Number) : Signal;

/// <summary>A query table's query changed (or its page was read again).</summary>
/// <param name="Filter">The filter text.</param>
/// <param name="SortBy">The column id the rows are ordered by, or null.</param>
/// <param name="Descending">True orders largest first.</param>
/// <param name="Page">The page shown.</param>
[Signal("control.tableChanged")]
public sealed record TableChanged(string Filter, string? SortBy, bool Descending, int Page) : Signal;
