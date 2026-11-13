using DigitalBrain.Access;
using IntoChat.OS;

namespace IntoChat.Cloud.Auth.Neurons;

// One principal's profile: preferences, the avatar picture and the welcome-mail marker. Keyed by principal so it
// survives a brain switch; the avatar lives in the same cluster grain storage every other Auth state uses.
[DigitalBrain.Contracts.NeuronAlias("auth.profile"), DigitalBrain.Contracts.DefaultNeuronType("auth.profile")]
public interface IAuthProfile
{
    const string Type = "auth.profile";

    static string Key(string principalId) => $"{NeuronKeys.SystemShard(principalId)}/auth/profile/{principalId}";

    [Internal]
    Task<ProfileView> Get();

    [Internal]
    Task Update(ProfileChange change);

    // True exactly once per principal: the caller that gets true owns sending the welcome mail.
    [Internal]
    Task<bool> ClaimWelcome();

    // The avatar is up to 5 MiB but one neuron call carries at most 1 MiB of arguments: it arrives in chunks of AvatarChunkBytes.
    // Nothing is stored until CommitAvatar; BeginAvatar discards an unfinished upload.
    const int AvatarChunkBytes = 512 * 1024;

    [Internal]
    Task BeginAvatar(string contentType);

    [Internal]
    Task AppendAvatar(byte[] chunk);

    [Internal]
    Task CommitAvatar();

    [Internal]
    Task SetAvatar(string contentType, byte[] bytes);

    [Internal]
    Task<AvatarImage?> GetAvatar();

    [Internal]
    Task RemoveAvatar();
}

public sealed record ProfileView(
    string? DisplayName,
    string Language,
    string Theme,
    string TimeZone,
    bool HasAvatar);

public sealed record ProfileChange(
    string DisplayName,
    string Language,
    string Theme,
    string TimeZone);

public sealed record AvatarImage(
    string ContentType,
    byte[] Bytes);
