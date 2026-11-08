using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
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
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null));
        RuntimeCallerContext.TryGet(out var caller);
        await Check("Publish", new PublisherIsSignedIn(caller?.PersonId));
        if (BrainScope.Create(caller!.AccountId, caller.BrainId).Id != BrainId())
        { throw new UnauthorizedAccessException("Only the brain that owns an app can publish it."); }
        // A listing is one app: the same name from another of the publisher's brains is refused,
        // never a silent takeover of what installers already trust.
        var listed = await Marketplace.Read(caller.PersonId, Name());
        var next = State.Clone();
        var manifest = next.Manifest!;
        next.PublishedVersion = next.Version;
        next.Published = new AppVersionRecord(next.Version, clock.GetUtcNow(), manifest, next.Spec, next.Files);
        next.Publisher = caller.PersonId;
        var releases = Releases();
        var release = Release(next.Published, caller.PersonId, new AppSource.Brain(caller.AccountId, caller.BrainId));
        var existing = releases.FirstOrDefault(item => item.Bundle.Version == next.Version);
        await Check("Publish",
            new PublicationIsNotForeign(listed?.Source.Scope(), BrainId()),
            new VersionContentIsUnchanged(existing is null || existing.Publication.Reference == release.Publication.Reference));
        next.Releases = existing is null ? [.. releases, release] : releases;
        if (existing is not null)
        { next.Published = next.Published with { SavedAt = existing.Publication.PublishedAt }; }
        var name = Name();
        await Save(next, new AppChanged(name, next.Version, next.Status), new AppPublished(name, next.Version));
        // State first: a failed listing write is repaired by publishing again, never the reverse
        // (a listing whose bundle Install cannot read).
        await Marketplace.Upsert(new MarketplaceListing(
            caller.PersonId, new AppSource.Brain(caller.AccountId, caller.BrainId), name, manifest.Title, manifest.Description,
            next.Version, manifest.Icon, manifest.Requires));
        return Describe(State);
    }

    public Task<AppBundle?> ExportVersion(string version)
    {
        var record = State.History.LastOrDefault(item => item.Version == version);
        return Task.FromResult(record is null ? null
            : new AppBundle(Name(), record.Version, record.Manifest, record.Spec, new(record.Files, StringComparer.Ordinal)));
    }

    public async Task<AppBundle> ExportPublished()
    {
        await Check("ExportPublished", new HasPublishedVersion(State.Published is not null));
        var published = State.Published!;
        return new AppBundle(Name(), published.Version, published.Manifest, published.Spec,
            new(published.Files, StringComparer.Ordinal));
    }

    public async Task<AppSnapshot> SaveToSource()
    {
        var development = ServiceProvider.GetService<IHostEnvironment>() is { } environment && environment.IsDevelopment();
        var fromFeature = State.Origin is { Source: AppSource.Shipped };
        await Check("SaveToSource",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null),
            new SaveToSourceIsAvailable(development, fromFeature, Name()));
        var origin = State.Origin!;
        var catalog = ServiceProvider.GetRequiredService<FeatureCatalog>();
        var exported = await Export();
        var shipped = await catalog.Find(origin.Publisher, origin.Name);
        var version = Bump(Newest(exported.Version, shipped?.Version ?? exported.Version), minor: false);
        var saved = await catalog.SaveToSource(origin.Publisher, exported with { Name = origin.Name, Version = version });
        var next = State.Clone();
        next.Version = saved.Version;
        next.Origin = origin with { Version = saved.Version };
        next.Edited = false;
        Record(next);
        await Persist(next);
        return Describe(State);
    }

    private static string Newest(string left, string right)
        => Version.Parse(left) >= Version.Parse(right) ? left : right;

    public Task<AppPublication[]> PublishedReleases()
        => Task.FromResult(Releases().Select(release => release.Publication).ToArray());

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
            || State.Principal is not { } principal
            || State.Releases.Any(release => release.Bundle.Version == held.Version))
        { return State.Releases; }
        return [.. State.Releases, Release(held, publisher, new AppSource.Brain(principal.AccountId, principal.BrainId))];
    }

    private AppReleaseRecord Release(AppVersionRecord version, string publisher, AppSource.Brain source)
    {
        var bundle = new AppBundle(Name(), version.Version, version.Manifest, version.Spec, new(version.Files, StringComparer.Ordinal));
        var preview = bundle.Files.GetValueOrDefault("preview.txt");
        if (preview is { Length: > 16384 }) { preview = preview[..16384]; }
        return new(new(new(publisher, source, Name(), version.Version, AppPublicationDigest.Of(bundle)),
            version.SavedAt, version.Manifest, preview), bundle);
    }
}
