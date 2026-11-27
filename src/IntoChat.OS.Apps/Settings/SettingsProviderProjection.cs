using System.Globalization;
using DigitalBrain.Capabilities.Connections;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Single source of truth for Settings AI provider rows, role assignment rows,
/// runtime/hardware status formatting, and vault/secret presence checks.
/// Shared by <see cref="SettingsApp"/> snapshots and <see cref="SettingsModelsAndWebView"/>
/// so visual catalog and fallback projection logic is never duplicated.
/// </summary>
internal static class SettingsProviderProjection
{
    public static readonly AiProvider[] DisplayedProviders =
    [
        AiProvider.Ollama,
        AiProvider.FoundryLocal,
        AiProvider.OpenAI,
        AiProvider.Anthropic,
        AiProvider.Google,
        AiProvider.XAI,
        AiProvider.OpenRouter,
    ];

    public static SettingsRoleRow[] BuildRoleRows(
        IReadOnlyList<ModelCard> cards,
        IReadOnlyDictionary<string, ModelAvailability> availability,
        RoleMap roleMap,
        string? personModel)
    {
        var allRoles = Enum.GetValues<ModelRole>();
        var rows = new SettingsRoleRow[allRoles.Length];
        for (var i = 0; i < allRoles.Length; i++)
        {
            var role = allRoles[i];
            var needed = RoleNeeds.Of(role);
            var deduped = new Dictionary<string, SettingsModelChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if ((card.Can & needed) != needed)
                {
                    continue;
                }

                var id = card.Id.Value;
                var available = availability.TryGetValue(id, out var avail) && avail.Ready;
                var reason = available ? null : avail?.Reason;
                var choice = new SettingsModelChoice(id, card.Name, available, reason);
                if (!deduped.TryGetValue(id, out var existing) || (!existing.Available && choice.Available))
                {
                    deduped[id] = choice;
                }
            }

            string? selected = null;
            var hasDevice = roleMap.Device.TryGetValue(role, out var deviceId);
            var hasCloud = roleMap.Cloud.TryGetValue(role, out var cloudId);
            if (hasDevice && availability.TryGetValue(deviceId.Value, out var deviceAvail) && deviceAvail.Ready)
            {
                selected = deviceId.Value;
            }
            else if (hasCloud && availability.TryGetValue(cloudId.Value, out var cloudAvail) && cloudAvail.Ready)
            {
                selected = cloudId.Value;
            }
            else if (hasDevice)
            {
                selected = deviceId.Value;
            }
            else if (hasCloud)
            {
                selected = cloudId.Value;
            }
            else if (role == ModelRole.Chat && !string.IsNullOrWhiteSpace(personModel))
            {
                selected = personModel.Trim();
            }

            rows[i] = new SettingsRoleRow(role, selected, [.. deduped.Values]);
        }

        return rows;
    }

    public static SettingsRoleRow[] DefaultRoles(SettingsAppState state)
    {
        var allRoles = Enum.GetValues<ModelRole>();
        var rows = new SettingsRoleRow[allRoles.Length];
        for (var i = 0; i < allRoles.Length; i++)
        {
            rows[i] = DefaultRoleRow(allRoles[i], state);
        }

        return rows;
    }

    public static SettingsRoleRow DefaultRoleRow(ModelRole role, SettingsAppState state)
    {
        var needed = RoleNeeds.Of(role);
        var choices = KnownModels.All
            .Where(card => (card.Can & needed) == needed)
            .Select(static card => new SettingsModelChoice(card.Id.Value, card.Name))
            .ToArray();
        var selected = role == ModelRole.Chat && !string.IsNullOrWhiteSpace(state.DefaultModel)
            ? state.DefaultModel
            : KnownModels.Defaults.Device.TryGetValue(role, out var deviceId)
                ? deviceId.Value
                : KnownModels.Defaults.Cloud.TryGetValue(role, out var cloudId)
                    ? cloudId.Value
                    : null;
        return new SettingsRoleRow(role, selected, choices);
    }

    public static SettingsProviderRow[] BuildProviderRows(
        IReadOnlyList<ModelCard> cards,
        IReadOnlyDictionary<string, ModelAvailability> availability,
        DeviceView? deviceView,
        string localEndpoint)
        => BuildProviderRowsCore(
            deviceView,
            localEndpoint,
            readyLocalOllamaCount: cards.Count(c =>
                c.Provider == AiProvider.Ollama
                && availability.TryGetValue(c.Id.Value, out var a)
                && a.Ready),
            isCloudProviderReady: provider => cards.Any(c =>
                c.Provider == provider
                && availability.TryGetValue(c.Id.Value, out var a)
                && a.Ready));

    public static SettingsProviderRow[] DefaultProviders(SettingsAppState state)
        => BuildProviderRowsCore(
            state.Device,
            state.LocalOllamaEndpoint,
            readyLocalOllamaCount: 0,
            isCloudProviderReady: provider => provider switch
            {
                AiProvider.OpenAI => HasVaultEntry(state, "OPENAI_API_KEY", "openai"),
                AiProvider.Anthropic => HasVaultEntry(state, "ANTHROPIC_API_KEY", "anthropic"),
                AiProvider.Google => HasVaultEntry(state, "GOOGLE_API_KEY", "google"),
                AiProvider.OpenRouter => HasVaultEntry(state, "OPENROUTER_API_KEY", "openrouter"),
                AiProvider.XAI => HasVaultEntry(state, "XAI_API_KEY", "xai"),
                _ => false,
            });

    private static SettingsProviderRow[] BuildProviderRowsCore(
        DeviceView? deviceView,
        string localEndpoint,
        int readyLocalOllamaCount,
        Func<AiProvider, bool> isCloudProviderReady)
    {
        var rows = new List<SettingsProviderRow>(DisplayedProviders.Length + (deviceView?.Runtimes.Length ?? 0));
        foreach (var provider in DisplayedProviders)
        {
            var visual = VisualCatalog.ForProvider(provider.ToString());
            var label = provider switch
            {
                AiProvider.Ollama => "Ollama",
                AiProvider.FoundryLocal => "Foundry Local",
                _ => visual.Label,
            };

            string status;
            if (provider == AiProvider.FoundryLocal)
            {
                var foundry = deviceView?.Runtimes.FirstOrDefault(static r => r.Id == RuntimeId.FoundryLocal);
                var installedCount = deviceView?.Models.Count(static m =>
                    m.Runtime == RuntimeId.FoundryLocal && m.State is InstallState.Ready or InstallState.Loaded) ?? 0;
                status = FormatRuntimeStatus(foundry, installedCount, fallbackUnavailable: "Not available");
            }
            else if (provider == AiProvider.Ollama)
            {
                var ollama = deviceView?.Runtimes.FirstOrDefault(static r => r.Id == RuntimeId.Ollama);
                var installedOnDevice = deviceView?.Models.Count(static m =>
                    m.Runtime == RuntimeId.Ollama && m.State is InstallState.Ready or InstallState.Loaded) ?? 0;
                status = ollama is not null
                    ? FormatRuntimeStatus(ollama, installedOnDevice, fallbackUnavailable: $"Not detected · {ollama.Endpoint?.ToString() ?? localEndpoint}")
                    : readyLocalOllamaCount > 0
                        ? $"Ready · {localEndpoint} · {readyLocalOllamaCount} installed"
                        : $"Not detected · {localEndpoint}";
            }
            else
            {
                status = isCloudProviderReady(provider) ? "Ready" : "Add a key in API keys";
            }

            rows.Add(new SettingsProviderRow(provider, label, visual.IconKey, status));

            if (provider == AiProvider.FoundryLocal && deviceView is not null)
            {
                var compatVisual = VisualCatalog.ForProvider(nameof(AiProvider.OpenAICompatible));
                foreach (var runtime in deviceView.Runtimes)
                {
                    if (runtime.Id == RuntimeId.Ollama || runtime.Id == RuntimeId.FoundryLocal)
                    {
                        continue;
                    }

                    var installedCount = deviceView.Models.Count(m =>
                        m.Runtime == runtime.Id && m.State is InstallState.Ready or InstallState.Loaded);
                    rows.Add(new SettingsProviderRow(
                        AiProvider.OpenAICompatible,
                        FormatAttachedRuntimeLabel(runtime.Id),
                        compatVisual.IconKey,
                        FormatRuntimeStatus(runtime, installedCount, fallbackUnavailable: FormatUnavailableRuntime(runtime))));
                }
            }
        }

        return [.. rows];
    }

    public static string FormatAttachedRuntimeLabel(RuntimeId id)
    {
        const string prefix = "openai-compat:";
        return id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && id.Value.Length > prefix.Length
            ? id.Value[prefix.Length..]
            : id.Value;
    }

    public static string FormatRuntimeStatus(RuntimeStatus? runtime, int installedCount, string fallbackUnavailable)
    {
        if (runtime is not { Reachable: true })
        {
            return fallbackUnavailable;
        }

        var parts = new List<string>(4) { "Ready" };
        if (!string.IsNullOrWhiteSpace(runtime.Version))
        {
            parts.Add($"v{runtime.Version.TrimStart('v', 'V')}");
        }
        if (runtime.Endpoint is not null)
        {
            parts.Add(runtime.Endpoint.ToString().TrimEnd('/'));
        }
        parts.Add($"{installedCount} installed");
        return string.Join(" · ", parts);
    }

    public static string FormatUnavailableRuntime(RuntimeStatus runtime)
        => runtime.Endpoint is not null
            ? $"Unreachable · {runtime.Endpoint.ToString().TrimEnd('/')}"
            : "Unreachable";

    public static string FormatHardwareLine(Hardware hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        const double gib = 1024d * 1024d * 1024d;
        var parts = new List<string>(3);
        if (hardware.RamBytes > 0)
        {
            var ramGb = Math.Max(1L, (long)Math.Round(hardware.RamBytes / gib, MidpointRounding.AwayFromZero));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ramGb} GB RAM"));
        }
        if (hardware.VramBytes > 0)
        {
            var vramGb = Math.Max(1L, (long)Math.Round(hardware.VramBytes / gib, MidpointRounding.AwayFromZero));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{vramGb} GB VRAM"));
        }

        var accelLabels = hardware.Accelerators
            .Where(static a => a != Accelerator.Cpu)
            .Select(static a => a switch
            {
                Accelerator.Cuda => "CUDA",
                Accelerator.DirectML => "DirectML",
                Accelerator.Npu => "NPU",
                Accelerator.Metal => "Metal",
                _ => a.ToString(),
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (accelLabels.Length > 0)
        {
            parts.Add(string.Join(", ", accelLabels));
        }
        else if (hardware.Accelerators.Contains(Accelerator.Cpu))
        {
            parts.Add("CPU");
        }

        return string.Join(" · ", parts);
    }

    public static bool IsSecretConfigured(SettingsAppState state, string secretName, string connectionKey)
        => (state.SecretStatuses != null && state.SecretStatuses.TryGetValue(secretName, out var configured) && configured)
           || HasVaultEntry(state, secretName, connectionKey);

    public static bool HasVaultEntry(SettingsAppState state, string variableKey, string connectionKey)
        => (state.Variables ?? []).Any(v => string.Equals(v.Key, variableKey, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Value))
           || (state.ActiveConnections ?? []).Any(c => string.Equals(c.ConnectorId, connectionKey, StringComparison.OrdinalIgnoreCase) && c.Status == ConnectionStatus.Connected);
}
