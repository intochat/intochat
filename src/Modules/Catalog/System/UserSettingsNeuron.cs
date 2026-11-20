using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("os.settings")]
public sealed class UserSettingsNeuron : DigitalBrain.Neurons.Neuron<UserSettingsState>, IUserSettings
{
    public Task<UserSettingsState> Read() => Task.FromResult(State);

    public async Task<UserSettingsState> SetProfile(string displayName, string theme, string defaultModel)
    {
        var nextDefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? State.DefaultModel : defaultModel.Trim();
        var nextRoles = State.Roles;
        if (!string.IsNullOrWhiteSpace(defaultModel) && nextRoles is { Length: > 0 })
        {
            var normalized = KnownModels.Find(nextDefaultModel)?.Id ?? new ModelId(nextDefaultModel);
            var list = nextRoles.ToList();
            var chatIndex = list.FindIndex(entry => entry.Role == Role.Chat);
            if (chatIndex >= 0)
            {
                list[chatIndex] = new RoleChoice(Role.Chat, normalized);
                nextRoles = [.. list];
            }
        }

        var next = State with
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? State.DisplayName : displayName.Trim(),
            Theme = string.IsNullOrWhiteSpace(theme) ? State.Theme : theme.Trim(),
            DefaultModel = nextDefaultModel,
            Roles = nextRoles,
        };
        await Save(next, new UserSettingsChanged(
            next.DisplayName,
            next.Theme,
            next.DefaultModel,
            next.StylePreset,
            next.BarLayout,
            next.ThemeMode,
            next.DesktopTone,
            next.Language,
            next.TimeZone,
            next.DateFormat));
        return next;
    }

    public async Task<UserSettingsState> SetUiIdentity(string stylePreset, string barLayout)
    {
        var next = State with
        {
            StylePreset = string.IsNullOrWhiteSpace(stylePreset) ? State.StylePreset : stylePreset.Trim(),
            BarLayout = string.IsNullOrWhiteSpace(barLayout) ? State.BarLayout : barLayout.Trim(),
        };
        await Save(next, new UserSettingsChanged(
            next.DisplayName,
            next.Theme,
            next.DefaultModel,
            next.StylePreset,
            next.BarLayout,
            next.ThemeMode,
            next.DesktopTone,
            next.Language,
            next.TimeZone,
            next.DateFormat));
        return next;
    }

    public async Task<UserSettingsState> SetPreferences(
        string? themeMode = null,
        string? desktopTone = null,
        string? language = null,
        string? timeZone = null,
        string? dateFormat = null,
        string? artCraftDefaultMode = null)
    {
        var normalizedArtCraftMode = string.IsNullOrWhiteSpace(artCraftDefaultMode)
            ? State.ArtCraftDefaultMode
            : artCraftDefaultMode.Trim().ToLowerInvariant() is "wasm-ui" or "mcp" or "native-linux"
                ? artCraftDefaultMode.Trim().ToLowerInvariant()
                : State.ArtCraftDefaultMode;

        var next = State with
        {
            ThemeMode = string.IsNullOrWhiteSpace(themeMode) ? State.ThemeMode : themeMode.Trim(),
            DesktopTone = string.IsNullOrWhiteSpace(desktopTone) ? State.DesktopTone : desktopTone.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? State.Language : language.Trim(),
            TimeZone = string.IsNullOrWhiteSpace(timeZone) ? State.TimeZone : timeZone.Trim(),
            DateFormat = string.IsNullOrWhiteSpace(dateFormat) ? State.DateFormat : dateFormat.Trim(),
            ArtCraftDefaultMode = normalizedArtCraftMode,
        };
        await Save(next, new UserSettingsChanged(
            next.DisplayName,
            next.Theme,
            next.DefaultModel,
            next.StylePreset,
            next.BarLayout,
            next.ThemeMode,
            next.DesktopTone,
            next.Language,
            next.TimeZone,
            next.DateFormat,
            next.ArtCraftDefaultMode));
        return next;
    }

    public async Task<UserSettingsState> SetVariable(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var variables = State.Variables.ToList();
        var index = variables.FindIndex(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
        if (index >= 0)
        {
            variables[index] = new UserVariableEntry(key, value);
        }
        else
        {
            variables.Add(new UserVariableEntry(key, value));
        }

        var next = State with { Variables = [.. variables] };
        await Save(next, new UserSettingsChanged(
            next.DisplayName,
            next.Theme,
            next.DefaultModel,
            next.StylePreset,
            next.BarLayout,
            next.ThemeMode,
            next.DesktopTone,
            next.Language,
            next.TimeZone,
            next.DateFormat));
        return next;
    }

    public async Task<UserSettingsState> SetRole(Role role, ModelId model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);

        var normalized = KnownModels.Find(model.Value)?.Id ?? new ModelId(model.Value.Trim());
        var roles = State.Roles is { Length: > 0 } existing ? existing.ToList() : new List<RoleChoice>();
        var index = roles.FindIndex(entry => entry.Role == role);
        if (index >= 0)
        {
            roles[index] = new RoleChoice(role, normalized);
        }
        else
        {
            roles.Add(new RoleChoice(role, normalized));
        }

        var next = State with
        {
            DefaultModel = role == Role.Chat ? normalized.Value : State.DefaultModel,
            Roles = [.. roles],
        };
        await Save(next, new UserSettingsChanged(
            next.DisplayName,
            next.Theme,
            next.DefaultModel,
            next.StylePreset,
            next.BarLayout,
            next.ThemeMode,
            next.DesktopTone,
            next.Language,
            next.TimeZone,
            next.DateFormat,
            next.ArtCraftDefaultMode));
        return next;
    }
}
