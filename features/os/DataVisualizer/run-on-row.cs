using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Postgres.Tables.Signals;

// Any row upsert in a source table refreshes every view; run-on-row-deleted.cs does the same for deletes.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var tables = Views.Registry<ITable>(brain, appKey, view => view.Source.StartsWith("apptable:", StringComparison.Ordinal)
    ? [view.Source[9..]] : []);
await brain.React<RowUpserted>(tables, _ => app.Request("refresh", ""), brain.Stopping);
