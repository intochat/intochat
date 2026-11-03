using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using DigitalBrain.State;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Circles.Rules;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Circles;

[ExportableState]
internal sealed class CircleFactoryRecord
{
    public CircleEntry[] Created { get; set; } = [];
}

// R126: `{brain}/circles` in a member's own brain. Create records a brain of kind Circle for the caller's account and
// gives it birth as its creator (the same birth BrainBirths gives: IBrain.Create stamped as the owner, inside the new
// brain); the circle brain's creation handler (CircleBirth) founds its core and installs its room.
// A plain neuron (06-08) at "{brain}/circles". The new circle brain is reached through IBrainCalls, as its creator acting there.
// The record of created circles is bookkeeping no one watches: it saves without a signal.
[DigitalBrain.Neuron("circles.factory")]
internal sealed class CircleFactoryNeuron(
    ICapabilityBroker broker,
    CircleBirth birth,
    IServiceProvider plain)
    : DigitalBrain.Neuron, ICircleFactory
{
    private CircleFactoryRecord Snapshot = new();

    private string Brain => Id.Brain.Value;

    private IServiceProvider ServiceProvider => Context.Services;

    protected override async Task OnActivate(CancellationToken cancellationToken)
        => Snapshot = await Context.State.Load<CircleFactoryRecord>() ?? new();

    private async Task SaveQuietly(CircleFactoryRecord next)
    {
        await Context.State.Save(next, []);
        Snapshot = next;
    }

    public Task BindPublicationButton()
    {
        Own();
        return SiloNeurons.Get<IntoChat.OS.Modules.Flutter.IUiBinding>(plain, IApp.Key(Brain, "circle") + "/fork-button")
            .Bind(IntoChat.OS.Modules.Flutter.UiHandlers.IdOf(Id));
    }

    public Task HandleUiEvent(IntoChat.OS.Modules.Flutter.UiEvent uiEvent)
    {
        Own();
        return PublicationButton.Handle(plain, Brain, "circle", "fork-button", uiEvent, Fork);
    }

    public async Task<string> Create(string name, string? requestId = null)
    {
        await Check("Create", new CircleNameIsValid(name));
        var caller = Own();
        var operation = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : $"circle|{requestId}";
        var directory = await broker.Identity();
        var provision = await directory.CreateBrain(caller.AccountId, caller.PersonId, operation, name, BrainKind.Circle);
        var circle = provision.Member.BrainId;
        // A retried Create completes a birth that failed before or inside the brain's creation: IBrain.Create is a no-op once the
        // brain exists, and a creation handler's failure is only logged there, so the birth runs again. The creator acts in the new
        // circle brain, a member of it since CreateBrain (06-08: IBrainCalls).
        await BrainActs.In(broker, circle, async () =>
        {
            await SiloNeurons.Get<IBrain>(plain, IBrain.Key(circle)).Create(new Owner(caller.AccountId));
            if (!provision.NewBrain) { await birth.Born(circle, CancellationToken.None); }
        });
        if (!Snapshot.Created.Any(created => created.BrainId == circle))
        { await SaveQuietly(new CircleFactoryRecord { Created = [.. Snapshot.Created, new(circle, name)] }); }
        return circle;
    }

    public async Task<CircleEntry[]> Circles()
    {
        var caller = Own();
        return [.. (await (await broker.Identity()).BrainsOf(caller.PersonId)).Where(entry => entry.Kind == BrainKind.Circle)
            .Select(entry => Snapshot.Created.FirstOrDefault(created => created.BrainId == entry.Id) ?? new CircleEntry(entry.Id, entry.Id))];
    }

    public async Task<CirclePublicationFeed> Publications(string? publisher = null)
    {
        var caller = Own();
        List<AppPublication> items = [];
        HashSet<string> unavailable = new(StringComparer.Ordinal);
        foreach (var listing in await SiloNeurons.Get<IMarketplace>(plain, IMarketplace.Key).List())
        {
            if (listing.Source is not AppSource.Brain source || publisher is { Length: > 0 } && listing.Publisher != publisher) { continue; }
            try
            {
                var releases = await SiloNeurons.Get<IntoChat.OS.Modules.Apps.IApp>(plain, IApp.Key(BrainScope.Create(source.AccountId, source.BrainId).Id, listing.Name)).PublishedReleases();
                items.AddRange(releases.Where(release => release.Reference.Publisher == listing.Publisher
                    && release.Reference.Source == source && release.Reference.Name == listing.Name));
            }
            catch (Exception error)
            {
                plain.GetRequiredService<ILogger<CircleFactoryNeuron>>().LogDebug(error, "A publisher feed could not be read.");
                unavailable.Add(listing.Publisher);
            }
        }
        return new([.. items.OrderByDescending(item => item.PublishedAt)
            .ThenBy(item => item.Reference.Publisher, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.Version, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.Digest, StringComparer.Ordinal)], [.. unavailable.Order(StringComparer.Ordinal)], caller.PersonId);
    }

    public async Task<AppSnapshot> Fork(AppPublicationRef publication, string targetName, Guid operationId)
    {
        Own();
        if (await (await broker.Identity()).KindOf(Brain) != BrainKind.Personal)
        { throw new UnauthorizedAccessException("Fork to my brain needs a personal brain. Use Bring into a circle for shared copies."); }
        return await ServiceProvider.GetRequiredService<AppInstaller>().InstallExact(ServiceProvider.GetRequiredService<INeuronProxies>(), publication, targetName, operationId);
    }

    // The member acts in its own brain.
    private CapabilityCaller Own()
    {
        if (!CallerContextStamper.TryGet(out var caller) || BrainScope.Create(caller.AccountId, caller.BrainId).Id != Brain)
        { throw new UnauthorizedAccessException($"{Brain}/{Id.Key} belongs to brain {Brain}."); }
        return caller;
    }
}
