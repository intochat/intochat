using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Kernel.OsRegistry;

/// <summary>
/// Production implementation of <see cref="IRegistryBroker"/> providing unified 3-tier OS registry access:
/// - <c>settings://{key}</c> -> User and system preferences managed by <see cref="IUserSettings"/>
/// - <c>env://{key}</c> -> Brain-scoped environment variables managed by <see cref="IBrainEntries"/>
/// - <c>vault://{key}</c> -> Sealed secrets managed by <see cref="IBrainEntries"/> with strict zero-leakage enforcement
/// </summary>
public sealed class RegistryBroker : IRegistryBroker
{
    private readonly IServiceProvider _services;
    private readonly ICapabilityBroker? _capabilityBroker;
    private readonly ConcurrentDictionary<string, string> _memoryFallback = new(StringComparer.OrdinalIgnoreCase);

    public RegistryBroker(IServiceProvider? services = null, ICapabilityBroker? capabilityBroker = null)
    {
        _services = services ?? new ServiceCollection().BuildServiceProvider();
        _capabilityBroker = capabilityBroker;
    }

    public ICapabilityBroker? CapabilityBroker => _capabilityBroker;

    public static (RegistryTier Tier, string Key) ParseUri(string uriString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriString);
        var colonIdx = uriString.IndexOf("://", StringComparison.Ordinal);
        if (colonIdx < 0)
        {
            throw new FormatException($"Invalid registry URI '{uriString}'. Expected scheme://key.");
        }

        var scheme = uriString[..colonIdx].Trim().ToLowerInvariant();
        var key = uriString[(colonIdx + 3)..].Trim().Trim('/');

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new FormatException($"Registry URI key cannot be empty in '{uriString}'.");
        }

        return scheme switch
        {
            "settings" => (RegistryTier.Settings, key),
            "env" => (RegistryTier.Environment, key),
            "vault" => (RegistryTier.Vault, key),
            _ => throw new NotSupportedException($"Scheme '{scheme}' is not supported by IRegistryBroker. Supported schemes: settings://, env://, vault://")
        };
    }

    public async Task<string?> GetAsync(string uri, CancellationToken ct = default)
    {
        var (tier, key) = ParseUri(uri);
        var brainId = ResolveCurrentBrainId();
        RuntimeCallerContext.TryGet(out var caller);

        var grainFactory = _services.GetService<IGrainFactory>();
        if (grainFactory is null)
        {
            if (!_memoryFallback.TryGetValue(uri, out var memVal))
            {
                return null;
            }
            if (tier == RegistryTier.Vault)
            {
                if (caller is not null && caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform)
                {
                    return memVal;
                }
                var tail = memVal.Length > 4 ? memVal[^4..] : memVal;
                return $"••••••••{tail}";
            }
            return memVal;
        }

        switch (tier)
        {
            case RegistryTier.Settings:
            {
                var settings = SiloNeurons.Get<IUserSettings>(_services, $"{brainId}/settings");
                var state = await settings.Read().ConfigureAwait(false);
                return ResolveSettingProperty(state, key);
            }

            case RegistryTier.Environment:
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                var items = await entries.List().ConfigureAwait(false);
                var match = items.FirstOrDefault(e => e.Kind == EntryKind.Variable && string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
                return match?.Value;
            }

            case RegistryTier.Vault:
            {
                // ZERO-RAW-TOKEN LEAKAGE ENFORCEMENT:
                // Non-platform callers NEVER receive raw secrets. Only trusted platform code carrying
                // the Platform stamp receives plaintext from Release.
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                var items = await entries.List().ConfigureAwait(false);
                var match = items.FirstOrDefault(e => e.Kind == EntryKind.Secret && string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
                if (match is null || !match.IsSet)
                {
                    return null;
                }

                if (caller is not null && caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform)
                {
                    return await entries.Release(caller, match.Name).ConfigureAwait(false);
                }

                // Masked token:
                return match.Tail is { Length: > 0 } tail ? $"••••••••{tail}" : "••••••••";
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(uri), $"Unsupported tier: {tier}");
        }
    }

    public async Task<T?> GetAsync<T>(string uri, CancellationToken ct = default)
    {
        var raw = await GetAsync(uri, ct).ConfigureAwait(false);
        if (raw is null)
        {
            return default;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)raw;
        }

        if (typeof(T) == typeof(bool))
        {
            return bool.TryParse(raw, out var b) ? (T)(object)b : default;
        }

        if (typeof(T) == typeof(int))
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? (T)(object)i : default;
        }

        if (typeof(T) == typeof(long))
        {
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? (T)(object)l : default;
        }

        if (typeof(T) == typeof(double))
        {
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (T)(object)d : default;
        }

        return JsonSerializer.Deserialize<T>(raw);
    }

    public async Task<RegistryEntryDescriptor?> DescribeAsync(string uri, CancellationToken ct = default)
    {
        var (tier, key) = ParseUri(uri);
        var brainId = ResolveCurrentBrainId();
        RuntimeCallerContext.TryGet(out var caller);

        var grainFactory = _services.GetService<IGrainFactory>();
        if (grainFactory is null)
        {
            if (!_memoryFallback.TryGetValue(uri, out var storedVal))
            {
                return null;
            }
            var isPlatform = caller is not null && caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform;
            if (tier == RegistryTier.Vault)
            {
                var val = isPlatform ? storedVal : storedVal.Length > 4 ? $"••••••••{storedVal[^4..]}" : "••••••••";
                var tail = storedVal.Length > 4 ? storedVal[^4..] : storedVal;
                return new RegistryEntryDescriptor(uri, key, RegistryTier.Vault, val, tail, true, DateTimeOffset.UtcNow);
            }
            return new RegistryEntryDescriptor(uri, key, tier, storedVal, null, true, DateTimeOffset.UtcNow);
        }

        switch (tier)
        {
            case RegistryTier.Settings:
            {
                var settings = SiloNeurons.Get<IUserSettings>(_services, $"{brainId}/settings");
                var state = await settings.Read().ConfigureAwait(false);
                var val = ResolveSettingProperty(state, key);
                return val is null ? null : new RegistryEntryDescriptor(uri, key, RegistryTier.Settings, val, null, true, null);
            }

            case RegistryTier.Environment:
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                var items = await entries.List().ConfigureAwait(false);
                var match = items.FirstOrDefault(e => e.Kind == EntryKind.Variable && string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
                return match is null || !match.IsSet ? null : new RegistryEntryDescriptor(uri, match.Name, RegistryTier.Environment, match.Value, null, true, match.UpdatedAt);
            }

            case RegistryTier.Vault:
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                var items = await entries.List().ConfigureAwait(false);
                var match = items.FirstOrDefault(e => e.Kind == EntryKind.Secret && string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
                if (match is null || !match.IsSet)
                {
                    return null;
                }

                string? val = caller is not null && caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform
                    ? await entries.Release(caller, match.Name).ConfigureAwait(false)
                    : match.Tail is { Length: > 0 } t ? $"••••••••{t}" : "••••••••";

                return new RegistryEntryDescriptor(uri, match.Name, RegistryTier.Vault, val, match.Tail, true, match.UpdatedAt);
            }

            default:
                return null;
        }
    }

    public async Task SetAsync(string uri, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (tier, key) = ParseUri(uri);
        var brainId = ResolveCurrentBrainId();

        _memoryFallback[uri] = value;
        var grainFactory = _services.GetService<IGrainFactory>();
        if (grainFactory is null)
        {
            return;
        }

        switch (tier)
        {
            case RegistryTier.Settings:
            {
                var settings = SiloNeurons.Get<IUserSettings>(_services, $"{brainId}/settings");
                if (IsKnownProfileSetting(key))
                {
                    await UpdateProfileSettingAsync(settings, key, value).ConfigureAwait(false);
                }
                else
                {
                    await settings.SetVariable(key, value).ConfigureAwait(false);
                }
                break;
            }

            case RegistryTier.Environment:
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                await entries.Put(key, EntryKind.Variable, value).ConfigureAwait(false);
                break;
            }

            case RegistryTier.Vault:
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                await entries.Put(key, EntryKind.Secret, value).ConfigureAwait(false);
                break;
            }
        }
    }

    public async Task RemoveAsync(string uri, CancellationToken ct = default)
    {
        var (tier, key) = ParseUri(uri);
        var brainId = ResolveCurrentBrainId();

        _memoryFallback.TryRemove(uri, out _);
        var grainFactory = _services.GetService<IGrainFactory>();
        if (grainFactory is null)
        {
            return;
        }

        switch (tier)
        {
            case RegistryTier.Settings:
            {
                var settings = SiloNeurons.Get<IUserSettings>(_services, $"{brainId}/settings");
                await settings.SetVariable(key, string.Empty).ConfigureAwait(false);
                break;
            }

            case RegistryTier.Environment:
            case RegistryTier.Vault:
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                await entries.Remove(key).ConfigureAwait(false);
                break;
            }
        }
    }

    public async Task<bool> ExistsAsync(string uri, CancellationToken ct = default)
    {
        var desc = await DescribeAsync(uri, ct).ConfigureAwait(false);
        return desc is not null && desc.IsSet;
    }

    public async Task<IReadOnlyList<RegistryEntryDescriptor>> ListAsync(string? schemeOrPrefix = null, CancellationToken ct = default)
    {
        var brainId = ResolveCurrentBrainId();
        RuntimeCallerContext.TryGet(out var caller);
        var isPlatform = caller is not null && caller.Kind == CallerKind.Platform && caller.StampedBy == TrustedEdge.Platform;

        var grainFactory = _services.GetService<IGrainFactory>();
        if (grainFactory is null)
        {
            var fallbackResults = new List<RegistryEntryDescriptor>();
            var filter = schemeOrPrefix?.Trim().TrimEnd('/', ':').ToLowerInvariant();

            foreach (var (storedUri, storedVal) in _memoryFallback)
            {
                var (entryTier, entryKey) = ParseUri(storedUri);
                if (!string.IsNullOrWhiteSpace(filter) && filter != "settings" && filter != "env" && filter != "vault" && !storedUri.StartsWith(schemeOrPrefix!, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if ((filter == "settings" && entryTier != RegistryTier.Settings) ||
                    (filter == "env" && entryTier != RegistryTier.Environment) ||
                    (filter == "vault" && entryTier != RegistryTier.Vault))
                {
                    continue;
                }

                if (entryTier == RegistryTier.Vault)
                {
                    var val = isPlatform ? storedVal : storedVal.Length > 4 ? $"••••••••{storedVal[^4..]}" : "••••••••";
                    var tail = storedVal.Length > 4 ? storedVal[^4..] : storedVal;
                    fallbackResults.Add(new RegistryEntryDescriptor(storedUri, entryKey, RegistryTier.Vault, val, tail, true, DateTimeOffset.UtcNow));
                }
                else
                {
                    fallbackResults.Add(new RegistryEntryDescriptor(storedUri, entryKey, entryTier, storedVal, null, true, DateTimeOffset.UtcNow));
                }
            }
            return fallbackResults;
        }

        var results = new List<RegistryEntryDescriptor>();
        var normalized = schemeOrPrefix?.Trim().TrimEnd('/', ':').ToLowerInvariant();

        var includeSettings = string.IsNullOrWhiteSpace(normalized) || normalized == "settings";
        var includeEnv = string.IsNullOrWhiteSpace(normalized) || normalized == "env";
        var includeVault = string.IsNullOrWhiteSpace(normalized) || normalized == "vault";

        if (includeSettings)
        {
            try
            {
                var settings = SiloNeurons.Get<IUserSettings>(_services, $"{brainId}/settings");
                var state = await settings.Read().ConfigureAwait(false);
                if (state is not null)
                {
                    AddSettingDescriptor(results, "displayName", state.DisplayName);
                    AddSettingDescriptor(results, "theme", state.Theme);
                    AddSettingDescriptor(results, "defaultModel", state.DefaultModel);
                    AddSettingDescriptor(results, "stylePreset", state.StylePreset);
                    AddSettingDescriptor(results, "barLayout", state.BarLayout);
                    AddSettingDescriptor(results, "themeMode", state.ThemeMode);
                    AddSettingDescriptor(results, "desktopTone", state.DesktopTone);
                    AddSettingDescriptor(results, "language", state.Language);
                    AddSettingDescriptor(results, "timeZone", state.TimeZone);
                    AddSettingDescriptor(results, "dateFormat", state.DateFormat);

                    if (state.Variables is not null)
                    {
                        foreach (var v in state.Variables)
                        {
                            if (!string.IsNullOrWhiteSpace(v.Key))
                            {
                                AddSettingDescriptor(results, v.Key, v.Value);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Silently skip if settings neuron is unavailable in test context
            }
        }

        if (includeEnv || includeVault)
        {
            try
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                var items = await entries.List().ConfigureAwait(false);
                foreach (var item in items)
                {
                    if (includeEnv && item.Kind == EntryKind.Variable)
                    {
                        results.Add(new RegistryEntryDescriptor(
                            $"env://{item.Name}",
                            item.Name,
                            RegistryTier.Environment,
                            item.Value,
                            null,
                            item.IsSet,
                            item.UpdatedAt));
                    }
                    else if (includeVault && item.Kind == EntryKind.Secret)
                    {
                        var val = isPlatform
                            ? await entries.Release(caller!, item.Name).ConfigureAwait(false)
                            : item.Tail is { Length: > 0 } t ? $"••••••••{t}" : "••••••••";

                        results.Add(new RegistryEntryDescriptor(
                            $"vault://{item.Name}",
                            item.Name,
                            RegistryTier.Vault,
                            val,
                            item.Tail,
                            item.IsSet,
                            item.UpdatedAt));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Silently skip if entries neuron is unavailable in test context
            }
        }

        return results;
    }

    private static void AddSettingDescriptor(List<RegistryEntryDescriptor> list, string key, string value)
    {
        list.Add(new RegistryEntryDescriptor(
            $"settings://{key}",
            key,
            RegistryTier.Settings,
            value,
            null,
            true,
            null));
    }

    private static string ResolveCurrentBrainId()
    {
        if (RuntimeCallerContext.TryGet(out var caller) && !string.IsNullOrWhiteSpace(caller?.BrainId))
        {
            return caller.BrainId;
        }

        try
        {
            var brain = BrainScope.CurrentId();
            if (!string.IsNullOrWhiteSpace(brain))
            {
                return brain;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _ = error.Message;
        }

        return "local";
    }

    private static string? ResolveSettingProperty(UserSettingsState state, string key)
    {
        if (state is null) { return null; }
        return key.ToLowerInvariant() switch
        {
            "displayname" or "name" => state.DisplayName,
            "theme" => state.Theme,
            "defaultmodel" or "model" => state.DefaultModel,
            "stylepreset" or "style" => state.StylePreset,
            "barlayout" or "layout" => state.BarLayout,
            "thememode" => state.ThemeMode,
            "desktoptone" or "tone" => state.DesktopTone,
            "language" or "lang" => state.Language,
            "timezone" or "tz" => state.TimeZone,
            "dateformat" => state.DateFormat,
            _ => state.Variables?.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))?.Value
        };
    }

    private static bool IsKnownProfileSetting(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower is "displayname" or "name" or "theme" or "defaultmodel" or "model"
            or "stylepreset" or "style" or "barlayout" or "layout"
            or "thememode" or "desktoptone" or "tone" or "language" or "lang"
            or "timezone" or "tz" or "dateformat";
    }

    private static async Task UpdateProfileSettingAsync(IUserSettings settings, string key, string value)
    {
        var lower = key.ToLowerInvariant();
        var current = await settings.Read().ConfigureAwait(false);
        switch (lower)
        {
            case "displayname" or "name":
                await settings.SetProfile(value, current.Theme, current.DefaultModel).ConfigureAwait(false);
                break;
            case "theme":
                await settings.SetProfile(current.DisplayName, value, current.DefaultModel).ConfigureAwait(false);
                break;
            case "defaultmodel" or "model":
                await settings.SetProfile(current.DisplayName, current.Theme, value).ConfigureAwait(false);
                break;
            case "stylepreset" or "style":
                await settings.SetUiIdentity(value, current.BarLayout).ConfigureAwait(false);
                break;
            case "barlayout" or "layout":
                await settings.SetUiIdentity(current.StylePreset, value).ConfigureAwait(false);
                break;
            case "thememode":
                await settings.SetPreferences(themeMode: value).ConfigureAwait(false);
                break;
            case "desktoptone" or "tone":
                await settings.SetPreferences(desktopTone: value).ConfigureAwait(false);
                break;
            case "language" or "lang":
                await settings.SetPreferences(language: value).ConfigureAwait(false);
                break;
            case "timezone" or "tz":
                await settings.SetPreferences(timeZone: value).ConfigureAwait(false);
                break;
            case "dateformat":
                await settings.SetPreferences(dateFormat: value).ConfigureAwait(false);
                break;
        }
    }
}
