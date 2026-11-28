using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Modules.Apps;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Desktop;

public sealed partial class DesktopApp
{
    [On]
    public async Task CreateSpecialist(DesktopCreateSpecialist request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = SpecialistAuthoring.PrepareCreate(
            new SpecialistDraft(
                request.Title,
                request.Handle,
                request.Instructions,
                request.Description,
                request.AllowedContracts,
                request.ToolSources,
                request.MemoryScope,
                request.Starters,
                request.Settings,
                request.Connections),
            await ReadExistingSpecialistsAsync(),
            brainId: TryCurrentBrain());
        await CompleteDesktopSpecialistAuthoringAsync(prepared, request.Settings, request.Connections);
    }

    [On]
    public async Task ImportSkill(DesktopImportSkill request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = SpecialistAuthoring.PrepareSkillImport(
            fileName: request.FileName,
            markdown: request.Content,
            existingSpecialists: await ReadExistingSpecialistsAsync(),
            handleOverride: request.Handle,
            allowedContractsOverride: request.AllowedContracts,
            toolSourcesOverride: request.ToolSources,
            memoryScopeOverride: request.MemoryScope,
            includeTregSlot: false,
            brainId: TryCurrentBrain());
        await CompleteDesktopSpecialistAuthoringAsync(prepared, settings: null, connections: null);
    }

    private async Task CompleteDesktopSpecialistAuthoringAsync(
        SpecialistAuthoringResult prepared,
        Dictionary<string, string>? settings,
        Dictionary<string, string>? connections)
    {
        if (!prepared.Succeeded || string.IsNullOrWhiteSpace(prepared.AppId) || prepared.Manifest is null)
        {
            var message = prepared.Error ?? "Could not validate specialist configuration.";
            await Show(State with { SpecialistError = message, Status = message });
            await RecordRejectionAsync(new Rejected("specialist-authoring", ["invalid_specialist"], [message]));
            return;
        }

        try
        {
            await SpecialistAuthoring.CreateAndConfigureAsync(prepared.AppId, prepared.Manifest, appId => Apps[$"apps/{appId}"], settings, connections);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "Desktop specialist authoring failed.");
            await Show(State with { SpecialistError = error.Message, Status = error.Message });
            await RecordRejectionAsync(new Rejected("specialist-authoring", ["specialist_create_failed"], [error.Message]));
            return;
        }

        RecordedRejection = null;
        await Show(State with { SpecialistError = null, Status = $"Created specialist {prepared.Handle ?? prepared.Title}" });
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
        await TryRouteSpecialistToInoAsync(prepared.AppId, forceAgent: true);
    }

    private async Task<bool> TryRouteSpecialistToInoAsync(string appId, bool forceAgent = false)
    {
        var isAgent = forceAgent
            || (State.Tiles ?? []).Any(t =>
                string.Equals(t.Id, appId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Kind, "agent", StringComparison.OrdinalIgnoreCase));

        if (!isAgent)
        {
            isAgent = await TryReadOptionalSlotAsync(
                async () => (await Apps[$"apps/{appId}"].Read())?.Manifest?.Agent is not null,
                fallback: false,
                $"Could not check whether app '{appId}' is a specialist.");
        }

        if (!isAgent) { return false; }

        await TryInvokeOptionalSlotAsync(async () =>
        {
            var command = Sends(new IntoChat.OS.Apps.Ino.InoOpenSpecialist(appId));
            await (InoScreen ?? Screens["app.ino"]).Dispatch(command);
        }, $"Could not route specialist '{appId}' to InoApp.");

        var brain = TryCurrentBrain();
        await ApplyDesktopWorkspaceChangeAsync(s => DesktopAppState.OpenWorkspaceApp(s, "ino", brain: brain), _ => null);
        return true;
    }

    private async Task<List<(string AppId, string? Handle)>> ReadExistingSpecialistsAsync()
    {
        var result = new List<(string AppId, string? Handle)>();
        var tiles = State.Tiles;
        if (tiles is null && InstalledApps is { } installedApps)
        {
            tiles = await TryReadOptionalSlotAsync<AppTile[]?>(
                async () => await installedApps.List(),
                fallback: null,
                "Could not list installed apps for handle uniqueness check.");
        }

        foreach (var tile in tiles ?? [])
        {
            if (!string.Equals(tile.Kind, "agent", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(tile.Id)) { continue; }
            var specialist = await TryReadOptionalSlotAsync<(bool Found, string? Handle)>(
                async () => (await Apps[$"apps/{tile.Id}"].Read())?.Manifest?.Agent is { } agent ? (true, agent.Handle) : (false, null),
                fallback: (false, null),
                $"Could not read specialist app '{tile.Id}'.");
            if (specialist.Found) { result.Add((tile.Id, specialist.Handle)); }
        }

        return result;
    }
}
