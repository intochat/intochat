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
    public async Task<ArtCraftEngineResult> ExecutePhotoCraft(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode)
    {
        await RequireAdmitted();
        var res = await ArtCraftEngineRunner.ExecutePhotoCraftAsync(
            instructionOrCommand,
            explicitParamsJson,
            currentDocument,
            executionMode,
            TryGetArtCraftStudio(),
            TryGetMcpServer);
        return Map(res);
    }

    public async Task<ArtCraftEngineResult> ExecuteFilmCraft(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        string executionMode)
    {
        await RequireAdmitted();
        var res = await ArtCraftEngineRunner.ExecuteFilmCraftAsync(
            instructionOrCommand,
            explicitParamsJson,
            currentSequence,
            executionMode,
            TryGetArtCraftStudio(),
            TryGetMcpServer);
        return Map(res);
    }

    private static ArtCraftEngineResult Map(ArtCraftExecutionResult res) => new(
        res.App,
        res.ExecutionMode,
        res.CommandId,
        res.CommandTitle,
        res.ParametersJson,
        res.InputDocument,
        res.OutputDocument,
        res.CliInvocation,
        res.Summary,
        res.PreviewDataUrl,
        res.LayersOrTracks,
        res.IsValidCommand,
        res.OutputNeuronUri);

    private IArtCraftStudio? TryGetArtCraftStudio()
    {
        try
        {
            return Get<IArtCraftStudio>(new DigitalBrain.NeuronId(Id.Brain, "artcraft", "artcraft"));
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
        var defaultPhotoCraftKey = IApp.Key(brain, ShippedApps.PhotoCraft);
        var defaultFilmCraftKey = IApp.Key(brain, ShippedApps.FilmCraft);
        var defaultInoKey = IApp.Key(brain, ShippedApps.Ino);
        var isShippedStudio = string.Equals(appId, ShippedApps.PhotoCraft, StringComparison.Ordinal)
            || string.Equals(appId, defaultPhotoCraftKey, StringComparison.Ordinal)
            || string.Equals(appId, ShippedApps.FilmCraft, StringComparison.Ordinal)
            || string.Equals(appId, defaultFilmCraftKey, StringComparison.Ordinal)
            || string.Equals(appId, ShippedApps.Ino, StringComparison.Ordinal)
            || string.Equals(appId, defaultInoKey, StringComparison.Ordinal);
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
        };
        if (appId.StartsWith(brain + "/apps/", StringComparison.Ordinal))
        {
            appGrantees.Add("app:" + appId[(brain.Length + "/apps/".Length)..]);
        }

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
