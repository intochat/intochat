using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.PersonPicture.Signals;

namespace IntoChat.OS.Modules.UI.PersonPicture;

[Neuron("ui.person-picture")]
internal sealed class PersonPictureNeuron : UiNeuronBase<PersonPictureState>, IPersonPicture
{
    public async Task<PersonPictureState> Configure(string displayName, string? subtitle = null, string? avatarUrl = null, string presence = "online")
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "Owner" : displayName.Trim();
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length >= 2
            ? $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}"
            : name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();
        var normPresence = NormalizePresence(presence);
        var next = State with
        {
            Version = State.Version + 1,
            DisplayName = name,
            Initials = initials,
            AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim(),
            Presence = normPresence,
            Subtitle = subtitle,
        };
        await Save(next, new PersonPictureChanged(Id.FullKey(), next.Version, next.Presence));
        return next;
    }

    public async Task<PersonPictureState> SetPresence(string presence)
    {
        var norm = NormalizePresence(presence);
        if (string.Equals(norm, State.Presence, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, Presence = norm };
        await Save(next, new PersonPictureChanged(Id.FullKey(), next.Version, next.Presence));
        return next;
    }

    public Task<PersonPictureState> Read() => Task.FromResult(State);

    private static string NormalizePresence(string? value) => (value ?? "online").Trim().ToLowerInvariant() switch
    {
        "online" or "busy" or "away" or "offline" => (value ?? "online").Trim().ToLowerInvariant(),
        _ => "online",
    };
}
