using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

// Coalesce invalidations, then read every retained run from the runtime. No app-owned
// history is written; one immutable tree replaces separate card, text and layout neurons.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await brain.React<BrainRunsChanged>(brain.Get<IBrainRuns>(BrainRunsKeys.For(BrainView.Scope(brain))), async _ =>
{
    try { await BrainView.PublishRecent(brain, await BrainView.ReadRecent(brain)); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("The recent runs could not be displayed: " + error.Message); }
}, brain.Stopping);
