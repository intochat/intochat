using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Postgres.Tables;
using DigitalBrain.Postgres.Tables.Signals;

// Any row upsert in a source table refreshes every view; run-on-row-deleted.cs does the same for deletes.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var tables = Views.Registry(brain, appKey, view => view.Source.StartsWith("apptable:", StringComparison.Ordinal)
    ? [brain.Get<ITable>(view.Source[9..])] : []);
await brain.React<RowUpserted>(tables, _ => app.Request("refresh", ""), brain.Stopping);
