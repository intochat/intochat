using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;

namespace IntoChat.OS.Kernel.Composition.Registry;

/// <summary>
/// Production implementation of <see cref="IRegistryBroker"/> providing unified 3-tier OS registry access:
/// - <c>settings://{key}</c> -> User and system preferences managed by <see cref="IUserSettings"/>
/// - <c>env://{key}</c> -> Brain-scoped environment variables managed by <see cref="IBrainEntries"/>
/// - <c>vault://{key}</c> -> Sealed secrets managed by <see cref="IBrainEntries"/> with strict zero-leakage enforcement
/// </summary>
public sealed class RegistryBroker : IRegistryBroker
{
    private readonly IServiceProvider? _services;
    private readonly ICapabilityBroker? _capabilityBroker;
    private readonly ConcurrentDictionary<string, string> _memoryFallback = new(StringComparer.OrdinalIgnoreCase);

    public RegistryBroker(IServiceProvider? services = null, ICapabilityBroker? capabilityBroker = null)
    {
        _services = services;
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
            _ => throw new NotSupportedException($"Scheme '{scheme}' is not supported by IRegistryBroker. Supported schemes: settings://, env://, vault://"),
        };
    }

    public async Task<string?> GetAsync(string uri, CancellationToken ct = default)
        => (await DescribeAsync(uri, ct).ConfigureAwait(false))?.Value;

    public async Task<T?> GetAsync<T>(string uri, CancellationToken ct = default)
    {
        var raw = await GetAsync(uri, ct).ConfigureAwait(false);
        if (raw is null) { return default; }
        if (typeof(T) == typeof(string)) { return (T)(object)raw; }
        if (typeof(T) == typeof(bool)) { return bool.TryParse(raw, out var b) ? (T)(object)b : default; }
        if (typeof(T) == typeof(int)) { return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? (T)(object)i : default; }
        if (typeof(T) == typeof(long)) { return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? (T)(object)l : default; }
        if (typeof(T) == typeof(double)) { return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (T)(object)d : default; }
        return JsonSerializer.Deserialize<T>(raw);
    }

    public async Task<RegistryEntryDescriptor?> DescribeAsync(string uri, CancellationToken ct = default)
    {
        var (tier, key) = ParseUri(uri);
        var isPlatform = CallerRules.IsKernel(CurrentCaller());

        if (_services?.GetService<IGrainFactory>() is null)
        {
            return _memoryFallback.TryGetValue(uri, out var storedVal)
                ? BuildMemoryDescriptor(uri, key, tier, storedVal, isPlatform)
                : null;
        }

        var brainId = ResolveCurrentBrainId();

        switch (tier)
        {
            case RegistryTier.Settings:
                {
                    var state = await SiloNeurons.Get<IUserSettings>(_services, IUserSettings.Key(new BrainId(brainId))).Read().ConfigureAwait(false);
                    var val = ResolveSettingProperty(state, key);
                    return val is null ? null : new RegistryEntryDescriptor(uri, key, RegistryTier.Settings, val, null, true, null);
                }
            case RegistryTier.Environment:
                {
                    var items = await SiloNeurons.Get<IBrainEntries>(_services, brainId).List().ConfigureAwait(false);
                    var match = items.FirstOrDefault(e => e.Kind == EntryKind.Variable && string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
                    return match is null || !match.IsSet ? null : new RegistryEntryDescriptor(uri, match.Name, RegistryTier.Environment, match.Value, null, true, match.UpdatedAt);
                }
            case RegistryTier.Vault:
                {
                    // ZERO-RAW-TOKEN LEAKAGE ENFORCEMENT:
                    // Non-platform callers NEVER receive raw secrets. Only trusted platform code carrying
                    // the Platform stamp receives plaintext from Release.
                    var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                    var items = await entries.List().ConfigureAwait(false);
                    var match = items.FirstOrDefault(e => e.Kind == EntryKind.Secret && string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
                    if (match is null || !match.IsSet) { return null; }

                    var val = isPlatform
                        ? await entries.Release(match.Name).ConfigureAwait(false)
                        : MaskTail(match.Tail);
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

        _memoryFallback[uri] = value;
        if (_services?.GetService<IGrainFactory>() is null) { return; }

        var brainId = ResolveCurrentBrainId();

        switch (tier)
        {
            case RegistryTier.Settings:
                {
                    var settings = SiloNeurons.Get<IUserSettings>(_services, IUserSettings.Key(new BrainId(brainId)));
                    if (IsKnownProfileSetting(key)) { await UpdateProfileSettingAsync(settings, key, value).ConfigureAwait(false); }
                    else { await settings.SetVariable(key, value).ConfigureAwait(false); }
                    break;
                }
            case RegistryTier.Environment:
                await SiloNeurons.Get<IBrainEntries>(_services, brainId).Put(key, EntryKind.Variable, value).ConfigureAwait(false);
                break;
            case RegistryTier.Vault:
                await SiloNeurons.Get<IBrainEntries>(_services, brainId).Put(key, EntryKind.Secret, value).ConfigureAwait(false);
                break;
        }
    }

    public async Task RemoveAsync(string uri, CancellationToken ct = default)
    {
        var (tier, key) = ParseUri(uri);

        _memoryFallback.TryRemove(uri, out _);
        if (_services?.GetService<IGrainFactory>() is null) { return; }

        var brainId = ResolveCurrentBrainId();

        switch (tier)
        {
            case RegistryTier.Settings:
                await SiloNeurons.Get<IUserSettings>(_services, IUserSettings.Key(new BrainId(brainId))).SetVariable(key, string.Empty).ConfigureAwait(false);
                break;
            case RegistryTier.Environment:
            case RegistryTier.Vault:
                await SiloNeurons.Get<IBrainEntries>(_services, brainId).Remove(key).ConfigureAwait(false);
                break;
        }
    }

    public async Task<bool> ExistsAsync(string uri, CancellationToken ct = default)
        => await DescribeAsync(uri, ct).ConfigureAwait(false) is { IsSet: true };

    public async Task<IReadOnlyList<RegistryEntryDescriptor>> ListAsync(string? schemeOrPrefix = null, CancellationToken ct = default)
    {
        var isPlatform = CallerRules.IsKernel(CurrentCaller());
        var normalized = schemeOrPrefix?.Trim().TrimEnd('/', ':').ToLowerInvariant();

        if (_services?.GetService<IGrainFactory>() is null)
        {
            var fallbackResults = new List<RegistryEntryDescriptor>();
            foreach (var (storedUri, storedVal) in _memoryFallback)
            {
                var (entryTier, entryKey) = ParseUri(storedUri);
                if (!string.IsNullOrWhiteSpace(normalized) && normalized is not ("settings" or "env" or "vault")
                    && !storedUri.StartsWith(schemeOrPrefix!, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if ((normalized == "settings" && entryTier != RegistryTier.Settings)
                    || (normalized == "env" && entryTier != RegistryTier.Environment)
                    || (normalized == "vault" && entryTier != RegistryTier.Vault))
                {
                    continue;
                }
                fallbackResults.Add(BuildMemoryDescriptor(storedUri, entryKey, entryTier, storedVal, isPlatform));
            }
            return fallbackResults;
        }

        var brainId = ResolveCurrentBrainId();

        var results = new List<RegistryEntryDescriptor>();
        var includeSettings = string.IsNullOrWhiteSpace(normalized) || normalized == "settings";
        var includeEnv = string.IsNullOrWhiteSpace(normalized) || normalized == "env";
        var includeVault = string.IsNullOrWhiteSpace(normalized) || normalized == "vault";

        if (includeSettings)
        {
            try
            {
                var state = await SiloNeurons.Get<IUserSettings>(_services, IUserSettings.Key(new BrainId(brainId))).Read().ConfigureAwait(false);
                if (state is not null)
                {
                    foreach (var (k, v) in new (string, string)[]
                    {
                        ("displayName", state.DisplayName), ("theme", state.Theme), ("defaultModel", state.DefaultModel),
                        ("stylePreset", state.StylePreset), ("barLayout", state.BarLayout), ("themeMode", state.ThemeMode),
                        ("desktopTone", state.DesktopTone), ("language", state.Language), ("timeZone", state.TimeZone),
                        ("dateFormat", state.DateFormat),
                    })
                    {
                        AddSettingDescriptor(results, k, v);
                    }
                    foreach (var v in (state.Variables ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Key)))
                    {
                        AddSettingDescriptor(results, v.Key, v.Value);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger().LogSwallowed(ex, $"Failed reading user settings registry entries for brain '{brainId}'.");
            }
        }

        if (includeEnv || includeVault)
        {
            try
            {
                var entries = SiloNeurons.Get<IBrainEntries>(_services, brainId);
                foreach (var item in await entries.List().ConfigureAwait(false))
                {
                    if (includeEnv && item.Kind == EntryKind.Variable)
                    {
                        results.Add(new RegistryEntryDescriptor($"env://{item.Name}", item.Name, RegistryTier.Environment, item.Value, null, item.IsSet, item.UpdatedAt));
                    }
                    else if (includeVault && item.Kind == EntryKind.Secret)
                    {
                        var val = isPlatform ? await entries.Release(item.Name).ConfigureAwait(false) : MaskTail(item.Tail);
                        results.Add(new RegistryEntryDescriptor($"vault://{item.Name}", item.Name, RegistryTier.Vault, val, item.Tail, item.IsSet, item.UpdatedAt));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger().LogSwallowed(ex, $"Failed reading env/vault registry entries for brain '{brainId}'.");
            }
        }

        return results;
    }

    private DigitalBrain.Access.Caller? CurrentCaller() => _services?.GetService<DigitalBrain.Access.ICallerAmbient>()?.Current;

    private static string MaskTail(string? tail) => tail is { Length: > 0 } ? $"••••••••{tail}" : "••••••••";

    private static RegistryEntryDescriptor BuildMemoryDescriptor(string uri, string key, RegistryTier tier, string storedVal, bool isPlatform)
    {
        if (tier != RegistryTier.Vault)
        {
            return new RegistryEntryDescriptor(uri, key, tier, storedVal, null, true, DateTimeOffset.UtcNow);
        }
        var tail = storedVal.Length > 4 ? storedVal[^4..] : storedVal;
        var val = isPlatform ? storedVal : $"••••••••{tail}";
        return new RegistryEntryDescriptor(uri, key, RegistryTier.Vault, val, tail, true, DateTimeOffset.UtcNow);
    }

    private static void AddSettingDescriptor(List<RegistryEntryDescriptor> list, string key, string value)
        => list.Add(new RegistryEntryDescriptor($"settings://{key}", key, RegistryTier.Settings, value, null, true, null));

    private string ResolveCurrentBrainId()
    {
        if (CurrentCaller() is { } caller)
        {
            return caller.Brain.Value;
        }
        try
        {
            var brain = BrainScope.CurrentId();
            if (!string.IsNullOrWhiteSpace(brain)) { return brain; }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<RegistryBroker>().LogSwallowed(error, "Could not resolve current BrainScope id.");
        }
        throw new InvalidOperationException("No BrainId is present on the caller or in BrainScope.");
    }

    private ILogger Logger() =>
        _services?.GetService<ILoggerFactory>()?.CreateLogger<RegistryBroker>() ?? OsConsoleLog.For<RegistryBroker>();

    private static string? ResolveSettingProperty(UserSettingsState state, string key) => state is null ? null : key.ToLowerInvariant() switch
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
        _ => state.Variables?.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))?.Value,
    };

    private static bool IsKnownProfileSetting(string key) => key.ToLowerInvariant() is
        "displayname" or "name" or "theme" or "defaultmodel" or "model"
        or "stylepreset" or "style" or "barlayout" or "layout"
        or "thememode" or "desktoptone" or "tone" or "language" or "lang"
        or "timezone" or "tz" or "dateformat";

    private static async Task UpdateProfileSettingAsync(IUserSettings settings, string key, string value)
    {
        var current = await settings.Read().ConfigureAwait(false);
        Task update = key.ToLowerInvariant() switch
        {
            "displayname" or "name" => settings.SetProfile(value, current.Theme, current.DefaultModel),
            "theme" => settings.SetProfile(current.DisplayName, value, current.DefaultModel),
            "defaultmodel" or "model" => settings.SetProfile(current.DisplayName, current.Theme, value),
            "stylepreset" or "style" => settings.SetUiIdentity(value, current.BarLayout),
            "barlayout" or "layout" => settings.SetUiIdentity(current.StylePreset, value),
            "thememode" => settings.SetPreferences(themeMode: value),
            "desktoptone" or "tone" => settings.SetPreferences(desktopTone: value),
            "language" or "lang" => settings.SetPreferences(language: value),
            "timezone" or "tz" => settings.SetPreferences(timeZone: value),
            "dateformat" => settings.SetPreferences(dateFormat: value),
            _ => Task.CompletedTask,
        };
        await update.ConfigureAwait(false);
    }
}
