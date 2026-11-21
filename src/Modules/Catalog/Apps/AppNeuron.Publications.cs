using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    public async Task<AppBundle> Export()
    {
        await RequireCreated("Export");
        var manifest = State.Manifest!;
        return new AppBundle(Name(), State.Version, manifest, State.Spec,
            new(State.Files, StringComparer.Ordinal));
    }

    public async Task<AppSnapshot> Publish()
    {
        await Check("Publish",
            new AppExists(Name(), State.Manifest));
        var person = Context.Caller?.For;
        await Check("Publish", new PublisherIsSignedIn(person?.Principal));
        if (person is null || !string.Equals(Context.Caller?.Brain.Value, BrainId(), StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("Only the brain that owns an app can publish it."); }
        // A listing is one app: the same name from another of the publisher's brains is refused,
        // never a silent takeover of what installers already trust.
        MarketplaceListing? listed;
        using (Edge?.Enter(Edge.Module("apps.catalog", new BrainId("system"))))
        {
            listed = await Marketplace.Read(person.Principal, Name());
        }
        var next = State.Clone();
        var manifest = next.Manifest!;
        next.PublishedVersion = next.Version;
        next.Published = new AppVersionRecord(next.Version, clock.GetUtcNow(), manifest, next.Spec, next.Files);
        next.Publisher = person.Principal;
        var releases = Releases();
        var release = PublishedRelease.Create(next.Published, person.Principal, new AppSource.Brain(person.Account, BrainId()), Name());
        var existing = releases.FirstOrDefault(item => item.Bundle.Version == next.Version);
        await Check("Publish",
            new PublicationIsNotForeign(listed?.Source.Scope(), BrainId()),
            new VersionContentIsUnchanged(existing, release.Reference));
        next.Releases = existing is null ? [.. releases, release.ToRecord()] : releases;
        if (existing is not null)
        { next.Published = next.Published with { SavedAt = existing.Publication.PublishedAt }; }
        var name = Name();
        await Save(next, new AppChanged(name, next.Version, next.Status), new AppPublished(name, next.Version));
        // State first: a failed listing write is repaired by publishing again, never the reverse
        // (a listing whose bundle Install cannot read).
        using (Edge?.Enter(Edge.Module("apps.catalog", new BrainId("system"))))
        {
            await Marketplace.Upsert(release.ToMarketplaceListing());
        }
        return Describe(State);
    }

    public async Task<AppBundle> ExportPublished()
    {
        await Check("ExportPublished", new HasPublishedVersion(State.Published));
        var published = State.Published!;
        return new AppBundle(Name(), published.Version, published.Manifest, published.Spec,
            new(published.Files, StringComparer.Ordinal));
    }

    public Task<AppBundle> ExportRelease(string version, string digest)
    {
        var release = Releases().FirstOrDefault(item => item.Publication.Reference.Version == version
            && item.Publication.Reference.Digest == digest)
            ?? throw new KeyNotFoundException($"Published release {Name()} {version} is unavailable.");
        return Task.FromResult(release.Bundle with { Files = new(release.Bundle.Files, StringComparer.Ordinal) });
    }

    private AppReleaseRecord[] Releases()
    {
        if (State.Published is not { } held || State.Publisher is not { } publisher
            || State.PublisherSource is not { } source
            || State.Releases.Any(release => release.Bundle.Version == held.Version))
        { return State.Releases; }
        var release = PublishedRelease.Create(held, publisher, new AppSource.Brain(source.Account, source.Brain), Name());
        return [.. State.Releases, release.ToRecord()];
    }
}
