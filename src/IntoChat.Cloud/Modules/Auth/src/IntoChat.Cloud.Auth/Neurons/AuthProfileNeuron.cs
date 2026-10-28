
namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neuron("auth.profile")]
internal sealed class AuthProfileNeuron(
    TimeProvider clock) : AuthNeuron<AuthProfileState>, IAuthProfile
{
    public Task<ProfileView> Get()
    {
        var state = Snapshot;
        return Task.FromResult(new ProfileView(state.DisplayName, state.Language, state.Theme, state.TimeZone, state.AvatarBytes is not null));
    }

    public async Task Update(ProfileChange change)
    {
        var next = Copy(Snapshot);
        next.DisplayName = change.DisplayName;
        next.Language = change.Language;
        next.Theme = change.Theme;
        next.TimeZone = change.TimeZone;
        await SaveQuietly(next);
    }

    public async Task<bool> ClaimWelcome()
    {
        if (Snapshot.WelcomeClaimedAt is not null) { return false; }
        var next = Copy(Snapshot);
        next.WelcomeClaimedAt = clock.GetUtcNow();
        await SaveQuietly(next);
        return true;
    }

    public async Task SetAvatar(string contentType, byte[] bytes)
    {
        var next = Copy(Snapshot);
        next.AvatarContentType = contentType;
        next.AvatarBytes = bytes;
        await SaveQuietly(next);
    }

    public Task<AvatarImage?> GetAvatar() =>
        Task.FromResult(Snapshot is { AvatarBytes: { } bytes, AvatarContentType: { } type } ? new AvatarImage(type, bytes) : null);

    public async Task RemoveAvatar()
    {
        var next = Copy(Snapshot);
        next.AvatarContentType = null;
        next.AvatarBytes = null;
        await SaveQuietly(next);
    }

    private static AuthProfileState Copy(AuthProfileState state) => new()
    {
        DisplayName = state.DisplayName,
        Language = state.Language,
        Theme = state.Theme,
        TimeZone = state.TimeZone,
        WelcomeClaimedAt = state.WelcomeClaimedAt,
        AvatarContentType = state.AvatarContentType,
        AvatarBytes = state.AvatarBytes,
    };
}
