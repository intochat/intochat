using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Postgres.Tables;
using DigitalBrain.Postgres.Tables.Signals;

// Any row delete in a source table refreshes every view; run-on-row.cs does the same for upserts.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var tables = Views.Registry(brain, appKey, view => view.Source.StartsWith("apptable:", StringComparison.Ordinal)
    ? [brain.Get<ITable>(view.Source[9..])] : []);
await brain.React<RowDeleted>(tables, _ => app.Request("refresh", ""), brain.Stopping);
