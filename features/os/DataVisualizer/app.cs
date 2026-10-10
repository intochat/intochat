/*
# Data Visualizer

A view is a source SELECT against the Postgres database bound to db, or an app-owned table,
rendered by generic table/chart neurons. The durable registry retains its definition; filter
field neurons retain current values. The workspace owns windows, never a local script cache.

SQL filters name output columns. The app quotes these identifiers and binds equality values
as positional parameters in an outer SELECT before the 200-row display limit. Blank means
all rows. Use base SQL without LIMIT when filters must search the whole source. A deliberate
SQL LIMIT defines a source subset (for example a top-ten aggregation).

A chart's first output column supplies labels and its second supplies finite numeric values.
Invalid values fail visibly rather than turning into invented zeroes. Both charts and tables
clear stale data after query failures. Empty results have an explicit status. Read returns
current chart points or table rows plus status after waiting for refresh.

| behavior | responsibility |
|----------|----------------|
| operations | Recover pending open/show/refine/read/close operations; compose reusable UI and manage the registry. A show invocation has a deterministic view ID, so replay cannot append another view. |
| query | Sole query/result writer. Drain durable refresh invocations, coalesce requests, read current filters and retry superseded reads before acknowledging. |
| run-on-change | Request refresh after durable registration and registry changes. |
| run-on-filter | Concurrently watch every filter; rebuild subscriptions when registry definitions change. |
| run-on-row | One upsert/delete subscription per distinct source table, refreshing all dependent views without competing readers. |
| run-on-tick | Watch each configured refresh reminder. |

Subscriptions reconcile only after registration. Process state contains no authoritative data.
Refresh delivery can repeat; it reads current source state and cannot create windows. Query
results cannot overtake one another because only the query behavior publishes them.

Refine adds visible output-column filters when needed rather than silently ignoring them.
Optional linkFiltersTo shares the existing source view's same-named filter neurons; changes
in either surface refresh every dependent view. Only columns declared by the source can be
linked. A chart may use an aggregate output column with the same name as a table filter.
SQL still defines the source scope: filtering a top-ten subset cannot reveal other groups.
Shared filter values remain durable through recomposition and behavior restart.

Refine validates string/null filter values and SQL output columns before changing state.
Optional filterColumns replaces the control list, including removing obsolete filters,
without deleting shared field neurons used by another view.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "datavisualizer";
    public const string Version = "1.1.6";

    public static readonly AppManifest Manifest = new(
        Title: "Data Visualizer",
        Description: "Live tables and charts over your data: SQL or app tables, filtered from the UI.",
        Operations:
        [
            new("open", "Compose the app surface listing every view; input \"docked\" skips the window."),
            new("show", "Create a view from JSON {title, kind: table|chart, source: sql|apptable:<id>, sql, filters: [outputColumn], refreshSeconds}. SQL is a SELECT without a row limit; the app applies equality filters with bound parameters before its 200-row display limit. Charts use the first output column as labels and the second as numeric values. The view id is derived from the request id; the table or chart neuron holds the result."),
            new("refine", "Set visible equality filters and optionally replace SQL: JSON {view, filters: {outputColumn: value}, sql, linkFiltersTo}. New filter columns become controls. Empty values clear filters. Optional linkFiltersTo is an existing view id whose same-named declared filters should be shared; changing either control refreshes both views. Prefer filters over embedding filter values in SQL. Optional filterColumns replaces the control list (empty removes all); SQL columns and value types are validated before saving."),
            new("read", "Refresh a view: input is the view id. The result is the view's table or chart neuron."),
            new("close", "Remove a view and its window: input is the view id."),
            new("refresh", "Refresh current source data and filter values; input is a view id or empty for all views."),
        ],
        Settings: [],
        Slots:
        [
            new("db", "postgres"),
        ],
        Icon: "icon.svg");
}
