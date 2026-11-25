using DigitalBrain.Components;
using DigitalBrain.Log;
using IntoChat.OS.Modules.Identity.Contracts.Handles;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("os.profile")]
public sealed class ProfileNeuron(ILogger<ProfileNeuron>? logger = null) : DigitalBrain.Neurons.Neuron<ProfileState>, IProfile
{
    private ILogger Log => (ILogger?)logger ?? OsConsoleLog.For<ProfileNeuron>();
    public Task<ProfileState> Read() => Task.FromResult(State);

    public Task<ProfileState> SetBio(string handle, string bio, string[] pinnedApps)
        => UpdateProfile(handle, State.DisplayName, bio, pinnedApps, State.AvatarUrl);

    public async Task<ProfileState> UpdateProfile(string handle, string displayName, string bio, string[] pinnedApps, string avatarUrl = "")
    {
        ArgumentNullException.ThrowIfNull(pinnedApps);
        var normalizedHandle = string.IsNullOrWhiteSpace(handle) ? State.Handle : handle.Trim().TrimStart('@');
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? (string.IsNullOrWhiteSpace(State.DisplayName) ? normalizedHandle : State.DisplayName)
            : displayName.Trim();
        var previousHandle = State.Handle;
        var next = State with
        {
            Handle = normalizedHandle,
            DisplayName = normalizedDisplayName,
            Bio = bio ?? "",
            AvatarUrl = avatarUrl ?? "",
            PinnedApps = [.. pinnedApps],
            Friends = State.Friends ?? [],
            SharedApps = State.SharedApps ?? [],
        };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        await TryClaimHandleAsync(previousHandle, normalizedHandle);
        return next;
    }

    private async Task TryClaimHandleAsync(string previousHandle, string nextHandle)
    {
        if (string.IsNullOrWhiteSpace(nextHandle) || string.IsNullOrWhiteSpace(Id.Brain.Value))
        {
            return;
        }
        if (Context.ClassOf(IHandleDirectory.TypeName) is null)
        {
            return;
        }

        var normalized = IHandleDirectory.Normalize(nextHandle);
        if (!IHandleDirectory.IsValid(normalized))
        {
            return;
        }

        var claimed = await HandleOf(normalized).Claim(Id.Brain.Value);

        if (claimed && !string.IsNullOrWhiteSpace(previousHandle))
        {
            var prevNormalized = IHandleDirectory.Normalize(previousHandle);
            if (!string.Equals(prevNormalized, normalized, StringComparison.Ordinal) && IHandleDirectory.IsValid(prevNormalized))
            {
                await HandleOf(prevNormalized).Release(Id.Brain.Value);
            }
        }
    }

    private IHandleDirectory HandleOf(string normalizedHandle)
    {
        var handleId = IHandleDirectory.IdOf(normalizedHandle);
        return Context.Get<IHandleDirectory>(new BindingTarget.Offered(handleId.Brain, handleId));
    }

    // Retired. Posts live in the post store (IPosts, "social.posts"), which copied what this profile kept; a post written
    // here after that copy would be seen by nothing. So these store nothing and say where to publish.
    public Task<ProfileState> PublishPost(string id, string content, Audience? audience = null) => throw PostsMoved();

    public Task<ProfileState> PublishAppPost(ProfilePostItem post) => throw PostsMoved();

    public Task<ProfileState> ForkPost(string postId, string newPostId, string? remixTitle = null) => throw PostsMoved();

    private static InvalidOperationException PostsMoved()
        => new("The profile no longer keeps posts and stored nothing: publish through the post store (IPosts.Publish on 'social.posts').");
}
