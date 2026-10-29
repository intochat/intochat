namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("os.profile")]
public sealed class ProfileNeuron : DigitalBrain.Neuron<ProfileState>, IProfile
{
    public Task<ProfileState> Read() => Task.FromResult(State);

    public async Task<ProfileState> SetBio(string handle, string bio, string[] pinnedApps)
    {
        ArgumentNullException.ThrowIfNull(pinnedApps);
        var next = State with
        {
            Handle = string.IsNullOrWhiteSpace(handle) ? State.Handle : handle.Trim(),
            Bio = bio ?? "",
            PinnedApps = [.. pinnedApps],
        };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }

    public async Task<ProfileState> PublishPost(string id, string content, string circle = "public")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var item = new ProfilePostItem(id, content.Trim(), Context.Time.GetUtcNow(), string.IsNullOrWhiteSpace(circle) ? "public" : circle.Trim());
        var posts = State.Posts.Where(existing => !string.Equals(existing.Id, id, StringComparison.Ordinal)).Prepend(item).Take(200).ToArray();
        var next = State with { Posts = posts };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }
}
