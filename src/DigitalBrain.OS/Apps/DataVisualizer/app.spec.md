# Data Visualizer

A view is a program: one SQL statement against the Postgres database connected to the `db` slot, or one
app-owned table, shown as a table or a chart. Filters are parameters of that program, never text pasted
into it. Every view has its own window: filter fields on top, the table or chart below, and a status line.
Views live in the app's registry neuron. Without a connected database a SQL view's status line reads
"Connect a Postgres database to this app."

| behavior | when | reacts |
|----------|------|--------|
| operations | the app is invoked (open, show, refine, read, close) | composes windows, edits the registry, answers |
| run-on-change | the view registry changes | every view runs again |
| run-on-filter | a view's filter field changes | that view runs again |
| run-on-row | a row of an app table behind a view is upserted or deleted | that view runs again |
| run-on-tick | a view's refresh reminder ticks | that view runs again |
