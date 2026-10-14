/*
# Data Visualizer

A view is a source SELECT against the Postgres database bound to db, or an app-owned table,
rendered by generic table/chart neurons. The durable registry retains its definition; filter
field neurons retain current values. All views are content of one published main layout, never separate windows.

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
| requests | Recover pending open/show/refine/read/close operations; compose reusable UI and manage the registry. A show invocation has a deterministic view ID, so replay cannot append another view; it adds the view to main. |
| refresh | Sole query/result writer. Drain durable refresh invocations, read current filters and retry superseded reads before acknowledging. |
| run-on-change | Request refresh after durable registration and registry changes. |
| run-on-filter | Concurrently watch every filter; rebuild subscriptions when registry definitions change. |
| run-on-row, run-on-row-deleted | One upsert (delete) subscription per distinct source table, refreshing all dependent views without competing readers. |
| run-on-tick | Watch each configured refresh reminder. |

Subscriptions reconcile only after registration. Process state contains no authoritative data.
Refresh delivery can repeat; it reads current source state and cannot change navigation. Query
results cannot overtake one another because only the query behavior publishes them.

Refine adds visible output-column filters when needed rather than silently ignoring them.
Optional linkFiltersTo shares the existing source view's same-named filter neurons; changes
in either view refresh every dependent view. Only columns declared by the source can be
linked. A chart may use an aggregate output column with the same name as a table filter.
SQL still defines the source scope: filtering a top-ten subset cannot reveal other groups.
Shared filter values remain durable through recomposition and behavior restart.

Refine validates string/null filter values and SQL output columns before changing state.
Optional filterColumns replaces the control list, including removing obsolete filters,
without deleting shared field neurons used by another view.

## Scenario: Opening the visualizer lists the views

Invoking "open" composes and publishes the main layout: its list card names every view,
followed by each view's filters, status and table or chart.

## Scenario: Show registers a view in main

Invoking "show" with {"title":"Numbers","kind":"table","source":"sql","sql":"select 1 as n"}
adds one view to the registry, writes its id to the last-view neuron, lists it in the list card,
composes its "Numbers" layout (status card, then table) as content of main.

## Scenario: A view whose source cannot be read fails visibly

With no Postgres connection bound to db, a SQL view's status card reads "Query failed" with
"Connect a Postgres database to this app." and its table holds no rows; a chart view's chart
holds no points.

## Scenario: A SQL view shows its rows

With a Postgres connection bound to db, a table view's table neuron holds the query's columns
and rows and its status card reads "Data loaded"; a chart view's chart holds one point per row,
labelled by the first column and valued by the second.

## Scenario: An app-table view follows its rows

A view with "source": "apptable:<id>" over a table this app owns shows the table's rows, and an
upsert or delete in that table re-runs every view over it.

## Scenario: Read refreshes a view

Invoking "read" with a view id re-runs its query and rewrites its status card. An unknown id
sets the app's status card to "Request failed" naming that id.

## Scenario: Refine adds a visible filter

Invoking "refine" with {"view": id, "filters": {"region": "EU"}} adds a "Region" text field holding
"EU" above the view's status card and records the filter column in the registry.

## Scenario: Typing in a filter refreshes every view sharing it

Typing into a view's filter field re-runs that view's query. After "refine" with
{"view": second, "filterColumns": ["region"], "linkFiltersTo": first}, typing into the first view's
Region field re-runs both views.

## Scenario: A refresh interval re-runs the view

A view shown with "refreshSeconds": 60 rewrites its status card every minute until closed. The
interval runs on a durable reminder, so a shorter positive interval is raised to one minute
rather than failing the show.

## Scenario: Close removes the view from main

Invoking "close" with a view id removes it from the registry and the list card, stops its
refresh reminder and removes its layout from main.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "datavisualizer";
    public const string Version = "1.1.14";

    public static readonly AppManifest Manifest = new(
        Title: "Data Visualizer",
        Description: "Live tables and charts over your data: SQL or app tables, filtered from the UI.",
        Operations:
        [
            new("open", "Compose and publish the main layout listing every view."),
            new("show", "Create a view from JSON {title, kind: table|chart, source: sql|apptable:<id>, sql, filters: [outputColumn], refreshSeconds (0 = off, otherwise at least 60)}. The view appears in the app main layout. SQL is a SELECT without a row limit; the app applies equality filters with bound parameters before its 200-row display limit. Charts use the first output column as labels and the second as numeric values. The view id is derived from the request id; the table or chart neuron holds the result."),
            new("refine", "Set visible equality filters and optionally replace SQL: JSON {view, filters: {outputColumn: value}, sql, linkFiltersTo}. New filter columns become controls. Empty values clear filters. Optional linkFiltersTo is an existing view id whose same-named declared filters should be shared; changing either control refreshes both views. Prefer filters over embedding filter values in SQL. Optional filterColumns replaces the control list (empty removes all); SQL columns and value types are validated before saving."),
            new("read", "Refresh a view: input is the view id. The result is the view's table or chart neuron."),
            new("close", "Remove a view from the main layout: input is the view id."),
            new("refresh", "Refresh current source data and filter values; input is a view id or empty for all views."),
        ],
        Settings: [],
        Slots:
        [
            new("db", "postgres"),
        ],
        Icon: "icon.svg");
}
