using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("apps.sideload")]
internal sealed class SideloadNeuron(SideloadInstaller? installer = null, IIdentityClient? identity = null) : DigitalBrain.Neuron, ISideload
{
    public async Task<SideloadRow[]> List()
    {
        await RequireAdmitted();
        if (installer is null) { return []; }
        return [.. await installer.List(CancellationToken.None)];
    }

    public async Task<SideloadResult> Install(string? name, string? consent = null)
    {
        await RequireAdmitted();
        if (installer is null) { return SideloadResult.Fail("This host has no apps directory."); }
        var res = await installer.Install(name, consent, CancellationToken.None);
        return new SideloadResult(res.Ok, res.Message);
    }

    public async Task<SideloadResult> AddGit(string? url)
    {
        await RequireAdmitted();
        if (installer is null) { return SideloadResult.Fail("This host has no apps directory."); }
        var res = await installer.AddGit(url, CancellationToken.None);
        return new SideloadResult(res.Ok, res.Message);
    }

    private async Task RequireAdmitted()
    {
        var brain = Id.Brain.Value;
        if (!CallerContextStamper.TryGet(out var caller))
        {
            return;
        }
        if (caller.Kind != CallerKind.App)
        {
            CallerAdmission.RequireSameBrain(brain, target: "apps.sideload");
            return;
        }
        if (!string.Equals(caller.BrainId, brain, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Access denied to apps.sideload in brain '{brain}'.");
        }

        if (!await IsAppGrantedAsync(brain, caller))
        {
            throw new UnauthorizedAccessException(
                $"App '{caller.AppId ?? caller.PrincipalId}' is not granted access to 'apps.sideload'.");
        }
    }

    private async Task<bool> IsAppGrantedAsync(string brain, CapabilityCaller caller)
    {
        var appId = caller.AppId ?? "";
        var defaultSettingsKey = IApp.Key(brain, "settings");
        if (identity is null)
        {
            return string.Equals(appId, "settings", StringComparison.Ordinal)
                || string.Equals(appId, defaultSettingsKey, StringComparison.Ordinal);
        }

        var grants = await identity.ListGrants(brain);
        if (grants.Length == 0)
        {
            return string.Equals(appId, "settings", StringComparison.Ordinal)
                || string.Equals(appId, defaultSettingsKey, StringComparison.Ordinal);
        }

        var targetPrefix = $"apps.sideload/{brain}/";
        var fullTarget = $"apps.sideload/{brain}/{Id.Key}";
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
            if (string.Equals(grant.Scope, "apps.sideload", StringComparison.Ordinal)
                || string.Equals(raw.SemanticTypeId, "apps.sideload", StringComparison.Ordinal))
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
