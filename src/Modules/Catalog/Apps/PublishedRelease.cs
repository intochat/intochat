using System.Text.RegularExpressions;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Domain model representing a published release of an application or runtime-authored unit in the catalog.
/// Encapsulates versioning, publication digest computation, publication metadata, and install receipt generation.
/// </summary>
internal sealed record PublishedRelease
{
    private static readonly Regex DigestRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    public const int MaxPreviewLength = 16384;

    public AppPublicationRef Reference { get; }
    public DateTimeOffset PublishedAt { get; }
    public AppManifest Manifest { get; }
    public string? Preview { get; }
    public AppBundle Bundle { get; }

    public string Publisher => Reference.Publisher;
    public AppSource.Brain Source => Reference.Source;
    public string Name => Reference.Name;
    public string Version => Reference.Version;
    public string Digest => Reference.Digest;

    public PublishedRelease(AppPublication publication, AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(publication.Reference);

        if (string.IsNullOrWhiteSpace(publication.Reference.Publisher))
        {
            throw new ArgumentException("Publication reference publisher cannot be empty.", nameof(publication));
        }

        if (string.IsNullOrWhiteSpace(publication.Reference.Name))
        {
            throw new ArgumentException("Publication reference name cannot be empty.", nameof(publication));
        }

        if (string.IsNullOrWhiteSpace(publication.Reference.Version))
        {
            throw new ArgumentException("Publication reference version cannot be empty.", nameof(publication));
        }

        if (!IsValidDigest(publication.Reference.Digest))
        {
            throw new ArgumentException($"Publication digest '{publication.Reference.Digest}' is invalid; must be 64 lowercase hex characters.", nameof(publication));
        }

        if (!string.Equals(publication.Reference.Version, bundle.Version, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Bundle version '{bundle.Version}' does not match publication reference version '{publication.Reference.Version}'.", nameof(bundle));
        }

        if (!string.Equals(publication.Reference.Name, bundle.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Bundle name '{bundle.Name}' does not match publication reference name '{publication.Reference.Name}'.", nameof(bundle));
        }

        Reference = publication.Reference;
        PublishedAt = publication.PublishedAt;
        Manifest = publication.Manifest;
        Preview = TruncatePreview(publication.Preview);
        Bundle = bundle with { Files = new Dictionary<string, string>(bundle.Files, StringComparer.Ordinal) };
    }

    public static PublishedRelease Create(AppVersionRecord version, string publisher, AppSource.Brain source, string appName)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        var bundle = new AppBundle(appName, version.Version, version.Manifest, version.Spec, new Dictionary<string, string>(version.Files, StringComparer.Ordinal));
        var digest = AppPublicationDigest.Of(bundle);
        var preview = TruncatePreview(bundle.Files.GetValueOrDefault("preview.txt"));
        var reference = new AppPublicationRef(publisher, source, appName, version.Version, digest);
        var publication = new AppPublication(reference, version.SavedAt, version.Manifest, preview);
        return new PublishedRelease(publication, bundle);
    }

    public static PublishedRelease FromBundle(AppBundle bundle, string publisher, AppSource.Brain source, DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentNullException.ThrowIfNull(source);

        var digest = AppPublicationDigest.Of(bundle);
        var preview = TruncatePreview(bundle.Files.GetValueOrDefault("preview.txt"));
        var reference = new AppPublicationRef(publisher, source, bundle.Name, bundle.Version, digest);
        var publication = new AppPublication(reference, publishedAt, bundle.Manifest, preview);
        return new PublishedRelease(publication, bundle);
    }

    public static PublishedRelease FromRecord(AppReleaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new PublishedRelease(record.Publication, record.Bundle);
    }

    public AppReleaseRecord ToRecord() => new(Publication, Bundle);

    public AppPublication Publication => new(Reference, PublishedAt, Manifest, Preview);

    public MarketplaceListing ToMarketplaceListing() =>
        new(
            Publisher,
            Source,
            Name,
            Manifest.Title,
            Manifest.Description,
            Version,
            Manifest.Icon,
            Manifest.Requires,
            AppPackageExtensions.TryFromFiles(Name, Version, Bundle.Files, Manifest.Title, Manifest.Description, Publisher));

    public AppInstallReceipt CreateInstallReceipt(Guid operationId, string targetName, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        }

        return new AppInstallReceipt(operationId, Reference, targetName, accountId);
    }

    public AppOrigin CreateOrigin(string accountId, AppRequirements? consented = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return new AppOrigin(Publisher, Source, Name, Version, Digest, accountId, consented);
    }

    public bool Matches(string version, string digest) =>
        string.Equals(Version, version, StringComparison.Ordinal)
        && string.Equals(Digest, digest, StringComparison.Ordinal);

    public bool HasSameContent(PublishedRelease other) =>
        other is not null && Reference == other.Reference;

    public void EnsureMatches(string expectedVersion, string expectedDigest)
    {
        if (!Matches(expectedVersion, expectedDigest))
        {
            throw new InvalidDataException($"Published release {Name} {Version} does not match expected version {expectedVersion} and digest {expectedDigest}.");
        }
    }

    public static bool IsReceiptMatching(
        AppInstallReceipt receipt,
        AppOrigin? origin,
        AppBundle bundle,
        string expectedTargetName,
        string expectedAccountId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(bundle);

        var reference = receipt.Publication;
        if (receipt.OperationId == Guid.Empty) { return false; }
        if (!string.Equals(receipt.TargetName, expectedTargetName, StringComparison.Ordinal)) { return false; }
        if (!string.Equals(receipt.AccountId, expectedAccountId, StringComparison.Ordinal)) { return false; }

        var expectedOrigin = new AppOrigin(reference.Publisher, reference.Source, reference.Name, reference.Version, reference.Digest, expectedAccountId);
        var normalizedOrigin = origin is null ? null : origin with { Consented = null };
        if (normalizedOrigin != expectedOrigin) { return false; }

        if (!string.Equals(bundle.Version, reference.Version, StringComparison.Ordinal)) { return false; }
        if (!string.Equals(AppPublicationDigest.Of(bundle), reference.Digest, StringComparison.Ordinal)) { return false; }

        return true;
    }

    public static bool IsValidDigest(string? digest) =>
        !string.IsNullOrWhiteSpace(digest) && digest.Length == 64 && DigestRegex.IsMatch(digest);

    public static string? TruncatePreview(string? preview) =>
        preview is { Length: > MaxPreviewLength } ? preview[..MaxPreviewLength] : preview;
}
