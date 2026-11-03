namespace IntoChat.OS.Modules.Apps;

public sealed record SideloadInstallResult(bool Ok, string Message)
{
    public static SideloadInstallResult Done(string message) => new(true, message);
    public static SideloadInstallResult Fail(string message) => new(false, message);
}

// Sideload install uses the same AppInstaller.Install the store uses, after the sandbox compile.
// Marketplace listing is not required: publisher sideload is the trust boundary, and the store
// returns in 0.8.0. There is no AppSource subtype for sideload.
public sealed class SideloadInstaller
{
    private readonly FeatureCatalog _catalog;
    private readonly GitSideload _git;
    private readonly Func<InstallRequest, CancellationToken, Task<InstallOutcome>> _install;
    private readonly Func<string, CancellationToken, Task<bool>>? _installed;

    public SideloadInstaller(FeatureCatalog catalog, GitSideload git, AppInstaller installer)
        : this(catalog, git, (request, cancellationToken) => installer.Install(request, cancellationToken)) { }

    public SideloadInstaller(FeatureCatalog catalog, GitSideload git, Func<InstallRequest, CancellationToken, Task<InstallOutcome>> install,
        Func<string, CancellationToken, Task<bool>>? installed = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(install);
        _catalog = catalog;
        _git = git;
        _install = install;
        _installed = installed;
    }

    public async Task<IReadOnlyList<SideloadRow>> List(CancellationToken cancellationToken)
    {
        await _catalog.RescanSideload().WaitAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SideloadRow>();
        foreach (var listing in (await _catalog.Listings().WaitAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.Publisher == FeatureCatalog.SideloadOwner)
            .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var installed = _installed is not null && await _installed(listing.Name, cancellationToken).ConfigureAwait(false);
            rows.Add(new(listing.Name, listing.Title, installed));
        }
        return rows;
    }

    public Task<SideloadInstallResult> Install(string? name, CancellationToken cancellationToken)
        => Install(name, consent: null, cancellationToken);

    public async Task<SideloadInstallResult> Install(string? name, string? consent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) { return SideloadInstallResult.Fail("Name an app."); }
        await _catalog.RescanSideload().WaitAsync(cancellationToken).ConfigureAwait(false);
        var failure = (await _catalog.Failures().WaitAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Owner == FeatureCatalog.SideloadOwner && item.Name == name);
        var bundle = await _catalog.Find(FeatureCatalog.SideloadOwner, name).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (bundle is null) { return SideloadInstallResult.Fail(failure?.Reason ?? $"{name} is not in the apps directory."); }
        try { await _catalog.AcceptSideload(bundle).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException error) { return SideloadInstallResult.Fail(error.Message); }

        var requires = bundle.Manifest.Requires ?? new AppRequirements();
        if (!AppConsent.IsEmpty(requires))
        {
            var expected = AppConsent.Of(requires);
            if (string.IsNullOrEmpty(consent) || !string.Equals(consent, expected, StringComparison.Ordinal))
            {
                return SideloadInstallResult.Fail($"Consent required: {name} requests capabilities that require human approval.");
            }
        }

        var outcome = await _install(new InstallRequest(new AppRef(FeatureCatalog.SideloadOwner, name), consent ?? AppConsent.Of(requires), Guid.NewGuid()), cancellationToken)
            .ConfigureAwait(false);
        return outcome.Ok
            ? SideloadInstallResult.Done(outcome.InstalledName is { Length: > 0 } installed ? $"Installed {installed}." : $"Installed {name}.")
            : SideloadInstallResult.Fail(outcome.Message ?? "Install did not finish.");
    }

    public async Task<SideloadInstallResult> AddGit(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) { return SideloadInstallResult.Fail("Name a git URL."); }
        try
        {
            var name = await _catalog.AddSideloadGit(_git, url, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            return SideloadInstallResult.Done($"Cloned {name}.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return SideloadInstallResult.Fail(error.Message); }
    }
}
