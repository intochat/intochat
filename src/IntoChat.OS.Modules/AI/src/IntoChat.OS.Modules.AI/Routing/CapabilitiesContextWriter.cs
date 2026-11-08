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
        foreach (var stale in (await context.Read()).Entries.Where(entry => !names.Contains(entry.Name))) { await context.Remove(stale.Name); }
        foreach (var entry in entries) { await context.Set(entry); }
    }

    public static Task Upsert(IAgentContext context, string app, MenuOperation[] operations)
        => operations.Length == 0 ? context.Remove(app) : context.Set(Entry(app, operations));

    private static ContextEntry Entry(string app, MenuOperation[] operations)
        => new(app, string.Join('\n', operations.Select(operation =>
            string.IsNullOrWhiteSpace(operation.Description) ? $"{app}.{operation.Name}" : $"{app}.{operation.Name}: {operation.Description}")), [.. AppContract]);
}
