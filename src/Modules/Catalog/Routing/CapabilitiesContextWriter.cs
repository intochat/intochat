using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.AI.Routing;

// The apps capabilities context: one entry per app, one line per operation, so an agent knows what each app can do
// without reading the menu. An app operation runs through IApp.Request (as RouterNeuron.Route does it), so that is the contract named.
internal static class CapabilitiesContextWriter
{
    private static readonly string[] AppContract = [typeof(IntoChat.OS.Modules.Apps.IApp).FullName!];

    public static ContextEntry[] Entries(IReadOnlyList<(string App, MenuOperation[] Operations)> apps)
        => [.. apps.Where(app => app.Operations.Length > 0).Select(app => Entry(app.App, app.Operations))];

    // The context holds exactly these apps afterwards: an app that left the brain loses its entry.
    public static async Task Replace(IAgentContext context, IReadOnlyList<(string App, MenuOperation[] Operations)> apps)
    {
        var entries = Entries(apps);
        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        // One read, as a fast path only: the context neuron's Upsert and Withdraw are the guard, inside its own turn.
        var held = (await context.Read()).Entries;
        var interrupts = held.Where(IsInterrupt).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in held.Where(entry => !names.Contains(entry.Name) && !interrupts.Contains(entry.Name))) { await context.Withdraw(stale.Name); }
        foreach (var entry in entries.Where(entry => !interrupts.Contains(entry.Name))) { await context.Upsert(entry); }
    }

    // An app's name never replaces or removes an interrupt entry a user or the platform wrote in this context; the neuron enforces it.
    public static async Task Upsert(IAgentContext context, string app, MenuOperation[] operations)
        => _ = await (operations.Length == 0 ? context.Withdraw(app) : context.Upsert(Entry(app, operations)));

    // The fast path's view of the neuron's rule. Only an interrupt a user or the platform wrote is protected; one a model or an app planted under an app's name is replaceable.
    private static bool IsInterrupt(ContextEntry entry)
        => entry.EffectiveTags.Contains(ContextTags.Interrupt) && entry.Provenance?.Trust is ContextTrust.User or ContextTrust.Platform;

    private static ContextEntry Entry(string app, MenuOperation[] operations)
        => new(app, string.Join('\n', operations.Select(operation =>
            string.IsNullOrWhiteSpace(operation.Description) ? $"{app}.{operation.Name}" : $"{app}.{operation.Name}: {operation.Description}")), [.. AppContract]);
}
