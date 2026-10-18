using IntoChat.OS.Apps;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Circles;

[GenerateSerializer, Alias("circles.factory-record")]
[ExportableState]
internal sealed class CircleFactoryRecord
{
    [Id(0)] public CircleEntry[] Created { get; set; } = [];
}

// R126: `{brain}/circles` in a member's own brain. Create records a brain of kind Circle for the caller's account and
// gives it birth as its creator (the same birth BrainBirths gives: IBrain.Create stamped as the owner, inside the new
// brain); the circle brain's creation handler (CircleBirth) founds its core and installs its room.
[GrainType("circles.factory")]
internal sealed class CircleFactoryNeuron(
    [PersistentState("circles.factory", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CircleFactoryRecord> store,
    IIdentityClient identity,
    CircleBirth birth)
    : Neuron<CircleFactoryRecord>(store), ICircleFactory
{
    private string Brain => this.GetPrimaryKeyString()[..^"/circles".Length];

    public Task BindPublicationButton()
    {
        Own();
        return GrainFactory.GetGrain<IntoChat.OS.Flutter.IUiBinding>(IApp.Key(Brain, "circle") + "/fork-button")
            .Bind(this.AsReference<ICircleFactory>());
    }

    public Task HandleUiEvent(Signal signal)
    {
        Own();
        return PublicationButton.Handle(GrainFactory, Brain, "circle", "fork-button", signal, Fork);
    }

    public async Task<string> Create(string name, string? requestId = null)
    {
        if (!ICircle.IsValidName(name)) { throw new ArgumentException("A circle name is 1 to 40 of a-z, 0-9 and '-'.", nameof(name)); }
        var caller = Own();
        var operation = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : $"circle|{requestId}";
        var provision = await identity.CreateBrain(caller.AccountId, caller.PrincipalId, operation, name, BrainKind.Circle);
        var circle = provision.Member.BrainId;
        CallerContextStamper.Stamp(caller with { BrainId = circle });
        try
        {
            // A retried Create completes a birth that failed before or inside the brain's creation: IBrain.Create is a
            // no-op once the brain exists, and a creation handler's failure is only logged there, so the birth runs again.
            await GrainFactory.GetGrain<IBrain>(circle).Create(new Owner(caller.AccountId));
            if (!provision.NewBrain) { await birth.Born(circle, CancellationToken.None); }
        }
        finally { CallerContextStamper.Stamp(caller); }
        if (!Snapshot.Created.Any(created => created.BrainId == circle))
        { await SaveQuietly(new CircleFactoryRecord { Created = [.. Snapshot.Created, new(circle, name)] }); }
        return circle;
    }

    public async Task<CircleEntry[]> Circles()
    {
        var caller = Own();
        return [.. (await identity.BrainsOf(caller.PrincipalId)).Where(entry => entry.Kind == BrainKind.Circle)
            .Select(entry => Snapshot.Created.FirstOrDefault(created => created.BrainId == entry.Id) ?? new CircleEntry(entry.Id, entry.Id))];
    }

    public async Task<CirclePublicationFeed> Publications(string? publisher = null)
    {
        var caller = Own();
        List<AppPublication> items = [];
        HashSet<string> unavailable = new(StringComparer.Ordinal);
        foreach (var listing in await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).List())
        {
            if (listing.Source is not AppSource.Brain source || publisher is { Length: > 0 } && listing.Publisher != publisher) { continue; }
            try
            {
                var releases = await GrainFactory.GetGrain<IApp>(IApp.Key(BrainScope.Create(source.AccountId, source.BrainId).Id, listing.Name)).PublishedReleases();
                items.AddRange(releases.Where(release => release.Reference.Publisher == listing.Publisher
                    && release.Reference.Source == source && release.Reference.Name == listing.Name));
            }
            catch (Exception) { unavailable.Add(listing.Publisher); }
        }
        return new([.. items.OrderByDescending(item => item.PublishedAt)
            .ThenBy(item => item.Reference.Publisher, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.Version, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.Digest, StringComparer.Ordinal)], [.. unavailable.Order(StringComparer.Ordinal)], caller.PrincipalId);
    }

    public async Task<AppSnapshot> Fork(AppPublicationRef publication, string targetName, Guid operationId)
    {
        Own();
        if (await identity.KindOf(Brain) != BrainKind.Personal)
        { throw new UnauthorizedAccessException("Fork to my brain needs a personal brain. Use Bring into a circle for shared copies."); }
        return await ServiceProvider.GetRequiredService<AppInstaller>().InstallPublication(publication, targetName, operationId, CancellationToken.None);
    }

    // The member acts in its own brain.
    private CallerContext Own()
    {
        if (!CallerContextStamper.TryGet(out var caller) || BrainScope.Create(caller.AccountId, caller.BrainId).Id != Brain)
        { throw new UnauthorizedAccessException($"{this.GetPrimaryKeyString()} belongs to brain {Brain}."); }
        return caller;
    }
}
