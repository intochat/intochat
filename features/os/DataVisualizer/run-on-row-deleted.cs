using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Postgres.Tables.Signals;

// Any row delete in a source table refreshes every view; run-on-row.cs does the same for upserts.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var tables = Views.Registry<ITable>(brain, appKey, view => view.Source.StartsWith("apptable:", StringComparison.Ordinal)
    ? [view.Source[9..]] : []);
await brain.React<RowDeleted>(tables, _ => app.Request("refresh", ""), brain.Stopping);
