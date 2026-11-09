using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("apps.artcraft-engine")]
internal sealed class ArtCraftEngineNeuron(IIdentityClient? identity = null) : DigitalBrain.Neuron, IArtCraftEngine
{
    public async Task<ArtCraftEngineResult> Execute(
        string app,
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode)
    {
        await RequireAdmitted();
        return await ArtCraftEngineRunner.ExecuteAsync(
            app,
            instructionOrCommand,
            explicitParamsJson,
            currentDocument,
            executionMode,
            TryGetArtCraftStudio(),
            TryGetMcpServer);
    }

    public Task<ArtCraftEngineResult> ExecutePhotoCraft(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode)
        => Execute(ShippedApps.PhotoCraft, instructionOrCommand, explicitParamsJson, currentDocument, executionMode);

    public Task<ArtCraftEngineResult> ExecuteFilmCraft(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        string executionMode)
        => Execute(ShippedApps.FilmCraft, instructionOrCommand, explicitParamsJson, currentSequence, executionMode);

    private IArtCraftStudio? TryGetArtCraftStudio()
    {
        try
        {
            return Get<IArtCraftStudio>(new DigitalBrain.NeuronId(Id.Brain, IArtCraftStudio.StudioKey, IArtCraftStudio.StudioKey));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private IMcpServer? TryGetMcpServer(string mcpKey)
    {
        try
        {
            var key = mcpKey.Contains('/', StringComparison.Ordinal)
                ? mcpKey[(mcpKey.IndexOf('/', StringComparison.Ordinal) + 1)..]
                : mcpKey;
            return Get<IMcpServer>(new DigitalBrain.NeuronId(Id.Brain, "mcp.server", key));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task RequireAdmitted()
    {
        var brain = Id.Brain.Value;
        if (!RuntimeCallerContext.TryGet(out var caller))
        {
            return;
        }
        if (caller.Kind != CallerKind.App)
        {
            CallerAdmission.RequireSameBrain(brain, target: "apps.artcraft-engine");
            return;
        }
        if (!string.Equals(caller.BrainId, brain, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Access denied to apps.artcraft-engine in brain '{brain}'.");
        }

        if (!await IsAppGrantedAsync(brain, caller))
        {
            throw new UnauthorizedAccessException(
                $"App '{caller.AppId ?? caller.PrincipalId}' is not granted access to 'apps.artcraft-engine'.");
        }
    }

    private async Task<bool> IsAppGrantedAsync(string brain, CapabilityCaller caller)
    {
        var appId = caller.AppId ?? "";
        var shortAppId = appId.StartsWith(brain + "/apps/", StringComparison.Ordinal)
            ? appId[(brain.Length + "/apps/".Length)..]
            : appId;
        var isShippedStudio = string.Equals(shortAppId, ShippedApps.Ino, StringComparison.Ordinal)
            || ArtCraftCatalog.IsArtCraftApp(shortAppId);
        if (identity is null)
        {
            return isShippedStudio;
        }

        var grants = await identity.ListGrants(brain);
        if (grants.Length == 0)
        {
            return isShippedStudio;
        }

        var targetPrefix = $"apps.artcraft-engine/{brain}/";
        var fullTarget = $"apps.artcraft-engine/{brain}/{Id.Key}";
        var appGrantees = new HashSet<string>(StringComparer.Ordinal)
        {
            caller.PrincipalId,
            $"app:{appId}",
            $"app:{brain}/apps/{appId}",
            $"app:{shortAppId}",
        };

        foreach (var raw in grants)
        {
            if (raw.Revoked) { continue; }
            var grant = Grant.Normalize(raw);
            if (!appGrantees.Contains(grant.Grantee) && !string.Equals(raw.AppId, appId, StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(grant.Scope, "apps.artcraft-engine", StringComparison.Ordinal)
                || string.Equals(raw.SemanticTypeId, "apps.artcraft-engine", StringComparison.Ordinal))
            {
                return true;
            }
            var target = grant.Target.StartsWith("neuron://", StringComparison.Ordinal) ? grant.Target["neuron://".Length..] : grant.Target;
            if (string.Equals(grant.Scope, GrantRelations.Run, StringComparison.Ordinal)
                && (string.Equals(target, "*", StringComparison.Ordinal)
                    || string.Equals(target, fullTarget, StringComparison.Ordinal)
                    || (target.EndsWith("/*", StringComparison.Ordinal) && fullTarget.StartsWith(target[..^1], StringComparison.Ordinal))
                    || target.StartsWith(targetPrefix, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
