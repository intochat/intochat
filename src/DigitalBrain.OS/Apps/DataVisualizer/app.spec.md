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
