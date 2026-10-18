using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Text.Signals;

// Follows the fast path's outcome on every target app: whenever a target app changes (CompleteSynapse
// publishes AppChanged beside SynapseWritten or SynapseRejected), every request still Writing is matched
// by its request id against that app's synapse list and takes the outcome from there. Rebuilt from state,
// so an outcome that landed while this script was away is read when it starts again.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var scope = Engineer.Scope(brain);
var targets = Sources.Named<IText, TextChanged>(Engineer.Targets(brain), async (registry, ct) =>
    [.. (await Engineer.Names(registry)).Select(name => brain.Get<IApp>(IApp.Key(scope, name)))]);

await brain.React<AppChanged>(targets, async ct =>
{
    var writing = (await Engineer.ReadAll(brain)).Where(request => request.Status == Engineer.Statuses.Writing).ToArray();
    foreach (var group in writing.GroupBy(request => request.Target, StringComparer.Ordinal))
    {
        var synapses = (await brain.Get<IApp>(IApp.Key(scope, group.Key)).Read()).Synapses ?? [];
        foreach (var request in group)
        {
            if (!Guid.TryParse(request.Id, out var operation)) { continue; }
            var entry = synapses.FirstOrDefault(synapse => synapse.OperationId == operation);
            if (entry is null || entry.Status == SynapseStatus.Requested) { continue; }
            if (entry.Status == SynapseStatus.Written)
            {
                var written = entry.RequestedAt.AddMilliseconds(entry.ElapsedMs);
                await Engineer.Save(brain, request with
                {
                    Status = Engineer.Statuses.Written, Path = entry.Path, WrittenAt = written, FastPathMs = entry.ElapsedMs,
                    TotalMs = (long)(written - request.ReceivedAt).TotalMilliseconds, Attempts = entry.Attempts,
                    Outcome = $"Live: {group.Key}/{entry.Path}" + (entry.Base is null ? "" : $" (forked from {entry.Base})"),
                });
            }
            else
            {
                await Engineer.Save(brain, request with
                {
                    Status = Engineer.Statuses.Rejected, Attempts = entry.Attempts, FastPathMs = entry.ElapsedMs,
                    Outcome = "The fast path could not write it: " + Engineer.Clip(string.Join("; ", entry.Errors), 600),
                });
            }
        }
    }
}, brain.Stopping);
