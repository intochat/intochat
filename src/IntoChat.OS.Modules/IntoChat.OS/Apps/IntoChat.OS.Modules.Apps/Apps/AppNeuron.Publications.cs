namespace IntoChat.OS.Apps;

internal sealed partial class AppNeuron
{
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
        if (Snapshot.Published is not { } held || Snapshot.Publisher is not { } publisher
            || Snapshot.Principal is not { } principal
            || Snapshot.Releases.Any(release => release.Bundle.Version == held.Version))
        { return Snapshot.Releases; }
        return [.. Snapshot.Releases, Release(held, publisher, new AppSource.Brain(principal.AccountId, principal.BrainId))];
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
