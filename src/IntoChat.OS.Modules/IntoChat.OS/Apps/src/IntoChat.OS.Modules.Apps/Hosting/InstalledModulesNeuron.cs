using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using System.Reflection;
using System.Text.Json;
using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("apps.installed-modules")]
public sealed class InstalledModulesNeuron(IIdentityClient? identity = null) : DigitalBrain.Neuron, IInstalledModules
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<InstalledModule[]> List()
    {
        await RequireAdmitted();
        return ListModules();
    }

    internal static InstalledModule[] ListModules()
    {
        var rows = new List<InstalledModule>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = [.. error.Types.Where(type => type is not null)!]; }
            catch (Exception error) when (error is not OperationCanceledException) { continue; }
            foreach (var type in types)
            {
                if (type is not { IsClass: true, IsAbstract: false } || type.ContainsGenericParameters) { continue; }
                if (type.GetCustomAttribute<DigitalBrain.ModuleAttribute>(inherit: false) is null) { continue; }
                if (!typeof(DigitalBrain.Module).IsAssignableFrom(type)) { continue; }
                DigitalBrain.ModuleManifest manifest;
                try { manifest = DigitalBrain.ModuleManifest.Of(type); }
                catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { continue; }
                var capabilities = string.Join(", ", manifest.Provides.Select(id => id.ToString()).Order(StringComparer.Ordinal));
                rows.Add(new(manifest.Id ?? type.Name, type.Name, capabilities));
            }
        }
        return [.. rows.OrderBy(row => row.Id, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal)];
    }

    internal static string Json() => JsonSerializer.Serialize(ListModules(), JsonOptions);

    private async Task RequireAdmitted()
    {
        var brain = Id.Brain.Value;
        if (!RuntimeCallerContext.TryGet(out var caller))
        {
            return;
        }
        if (caller.Kind != CallerKind.App)
        {
            CallerAdmission.RequireSameBrain(brain, target: "apps.installed-modules");
            return;
        }
        if (!string.Equals(caller.BrainId, brain, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Access denied to apps.installed-modules in brain '{brain}'.");
        }

        if (!await IsAppGrantedAsync(brain, caller))
        {
            throw new UnauthorizedAccessException(
                $"App '{caller.AppId ?? caller.PrincipalId}' is not granted access to 'apps.installed-modules'.");
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

        var targetPrefix = $"apps.installed-modules/{brain}/";
        var fullTarget = $"apps.installed-modules/{brain}/{Id.Key}";
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
            if (string.Equals(grant.Scope, "apps.installed-modules", StringComparison.Ordinal)
                || string.Equals(raw.SemanticTypeId, "apps.installed-modules", StringComparison.Ordinal))
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
