# CSV explorer

Explores comma-separated tabular data with live row filtering.

- `csv.load {"csv": "col1,col2\nval1,val2"}` parses the header row into `Columns` and the remaining lines into `Rows` (with optional `"title"`).
- `csv.filter {"value": "..."}` (or `{"filter": "..."}`) updates `Filter` to narrow visible rows in the table.
- The view renders a heading, a stat card with the matching row count, a data table of filtered rows, and a `filter` field that submits `csv.filter`.
