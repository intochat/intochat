# Data Visualizer

A view is a program: one SQL statement against the brain's Postgres, or one app-owned table, shown
as a table or a chart. Filters are parameters of that program, never text pasted into it. Every
view has its own window: filter fields on top, the table or chart below, and a status line. Views
live in the app's registry neuron, so restarting the app rebuilds every window exactly once.

## Scenario: Showing a SQL view opens a window with the rows

Invoking "show" with a table view over `select * from (values ('us', 2), ('uk', 1)) as t(country, n)`
answers a view id and a window naming the view's surface; the view's table then holds two rows with
columns country and n.

## Scenario: Typing a filter value replaces the rows

Given a view whose SQL is `select * from (values ('us', 2), ('uk', 1)) as t(country, n) where country = $1`
with filter column country, setting the country filter field to `uk` leaves exactly one row, `uk`,
and setting it to `u's; drop table t` leaves no rows and no error.

## Scenario: A chart view renders points from an aggregate query

Invoking "show" with a chart view over `select country, sum(n) from (values ('us', 2), ('us', 3), ('uk', 1)) as t(country, n) group by 1 order by 1`
yields a bar chart with two points: uk → 1 and us → 5.

## Scenario: Changing an app table re-renders its view

Given a table view over an app-owned table `sample` with columns id, country, inserting a row
through the table neuron adds it to the view's table without any invocation.

## Scenario: Restarting the behaviors rebuilds the same windows

After two views exist, invoking "open" twice answers the same two view ids each time, and each
view's surface revision changes at most once more.

## Scenario: An unreachable source keeps the last rows and reports the error

Given a view that has rendered rows, refining it so the SQL targets a table that does not exist
leaves the previous rows in place and sets the status card's body to the error.

## Scenario: Closing a view removes it

Invoking "close" with a view id removes it from the registry, and "read" for that id answers an error.

## Scenario (live): A platform table shows live data

With a real Postgres connection, a view over `select * from information_schema.tables limit 5`
renders five rows.
