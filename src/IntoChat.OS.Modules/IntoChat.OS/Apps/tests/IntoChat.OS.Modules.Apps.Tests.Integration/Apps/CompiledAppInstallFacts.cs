using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Reflection;
using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

public sealed class CompiledAppInstallFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Trait("host", "own")]
    [Fact]
    public async Task OwnerHttpReviewUsesTheUploadedDigestWithoutLoadingCode()
    {
        await using var fixture = await Fixture.Start();
        var package = await fixture.Package();
        using var upload = new ByteArrayContent(await File.ReadAllBytesAsync(fixture.Archive(package.Digest), Ct));
        upload.Headers.ContentType = new("application/octet-stream");
        using var staged = await fixture.Person.Http.PostAsync($"/brains/{fixture.Person.BrainId}/apps/packages", upload, Ct);
        staged.EnsureSuccessStatusCode();
        using var response = await fixture.Person.Http.PostAsJsonAsync($"/brains/{fixture.Person.BrainId}/apps/packages/review",
            new { package.Digest, Name = "counter" }, Ct);
        response.EnsureSuccessStatusCode();
        var review = await response.Content.ReadFromJsonAsync<CompiledAppReview>(Ct);
        Assert.NotNull(review);
        Assert.Equal(package.Digest, review.Digest);
        Assert.Equal(fixture.Person.BrainId, review.Brain);
        Assert.False(review.ReplacesExisting);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(AppConsent.Of(review.Requires), document.RootElement.GetProperty("requirementsConsent").GetString());
        Assert.Equal(CompiledAppReview.TrustMessage, document.RootElement.GetProperty("trustMessage").GetString());
        using var caller = fixture.Person.AsCaller();
        Assert.Equal("", (await fixture.App.Read()).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEmptyPackageNeedsOwnerTrustBeforeAnyAppOrGrantExists()
    {
        await using var fixture = await Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        var package = await fixture.Package();
        var review = await fixture.Installer.Prepare(package.Digest, "counter", Ct);
        Assert.False(review.ReplacesExisting);
        var approval = Approve(review);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
            approval with { TrustCode = false }, "counter", review.ExpectedRevision, new Dictionary<string, string>(), Ct));
        Assert.Equal("", (await fixture.App.Read()).Manifest.Title);
        var authorized = await fixture.Installer.Authorize(approval, "counter", review.ExpectedRevision, new Dictionary<string, string>(), Ct);
        Assert.Equal(package.Digest, authorized.Receipt.Digest);
        Assert.Equal(fixture.Person.PrincipalId, authorized.Receipt.PrincipalId);
        Assert.Equal(approval.OperationId, authorized.Receipt.OperationId);
        Assert.Equal("", (await fixture.App.Read()).Manifest.Title);
        // EntryType deliberately does not exist: neither review nor authorization may execute/reflect the DLL.
        Assert.Equal("NeverLoaded", authorized.Package.Descriptor.EntryType);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task MissingCapabilityLeavesTheDestinationUncreated()
    {
        await using var fixture = await Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        var package = await fixture.Package("storage@1");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Installer.Prepare(package.Digest, "counter", Ct));
        Assert.Contains("storage@1", error.Message, StringComparison.Ordinal);
        Assert.Equal("", (await fixture.App.Read()).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task CurrentOwnershipIsRecheckedAfterReview()
    {
        await using var fixture = await Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        var package = await fixture.Package();
        var review = await fixture.Installer.Prepare(package.Digest, "counter", Ct);
        fixture.Ownership.Revoked = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
            Approve(review), "counter", review.ExpectedRevision, new Dictionary<string, string>(), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Prepare(package.Digest, "counter", Ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReplacementReviewBindsTheCurrentProgramRevision()
    {
        await using var fixture = await Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        await fixture.App.Create(new("Counter", "", [new("open", "Open")], [], []), "## Scenario: Opens\nIt opens.");
        var package = await fixture.Package();
        var review = await fixture.Installer.Prepare(package.Digest, "counter", Ct);
        Assert.True(review.ReplacesExisting);
        Assert.Equal((await fixture.App.Read()).ProgramRevision, review.ExpectedRevision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
            Approve(review) with { ReplaceExisting = false }, "counter", review.ExpectedRevision, new Dictionary<string, string>(), Ct));
        var changed = await fixture.App.Configure(new() { ["choice"] = "preserved" }, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Installer.Authorize(
            Approve(review), "counter", changed.ProgramRevision, changed.Connections, Ct));
        Assert.Equal("preserved", (await fixture.App.Read()).Settings["choice"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ModelOriginatedOwnerCannotPrepareTrustedCode()
    {
        await using var fixture = await Fixture.Start();
        var package = await fixture.Package();
        using var caller = fixture.Person.AsModelImpersonatingCaller("conversation");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Prepare(package.Digest, "counter", Ct));
    }

    private static CompiledAppApproval Approve(CompiledAppReview review)
        => new(review.Token, review.Digest, Guid.NewGuid(), true, AppConsent.Of(review.Requires), true);

    private sealed class Fixture(ITestBrain brain, Person person, TestHome home, CompiledAppPackages packages, IIdentityClient identity) : IAsyncDisposable
    {
        internal ITestBrain Brain { get; } = brain;
        internal Person Person { get; } = person;
        internal OwnershipView Ownership => (OwnershipView)identity;
        internal IApp App => Brain.Get<IApp>(IApp.Key(Person.BrainId, "counter"));
        internal string Archive(string digest) => Path.Combine(home.Path, "packages", "archives", digest + ".intochat-app");
        internal CompiledAppInstaller Installer { get; } = new(brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>(),
            ActivatorUtilities.CreateInstance<AppInstaller>(brain.Services, identity), packages, new(TimeProvider.System), TimeProvider.System);

        internal static async Task<Fixture> Start()
        {
            var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(Ct);
            var person = await brain.PersonAsync("compiled-owner", Ct);
            var home = TestHome.Create();
            var identity = DispatchProxy.Create<IIdentityClient, OwnershipView>();
            ((OwnershipView)identity).Inner = brain.Services.GetRequiredService<IIdentityClient>();
            return new(brain, person, home, new(Path.Combine(home.Path, "packages"), new Dictionary<string, string>()), identity);
        }

        internal async Task<ValidatedAppPackage> Package(params string[] capabilities)
        {
            var build = Directory.CreateDirectory(Path.Combine(home.Path, "build")).FullName;
            var assembly = typeof(CompiledAppInstallFacts).Assembly;
            var entry = Path.GetFileName(assembly.Location);
            File.Copy(assembly.Location, Path.Combine(build, entry), overwrite: true);
            var descriptor = new CompiledAppDescriptor
            {
                Id = "counter",
                Name = "Counter",
                Version = "1",
                EntryAssembly = entry,
                EntryType = "NeverLoaded",
                Capabilities = [.. capabilities],
                Presentation = ImmutableSortedDictionary<string, string>.Empty.Add("operations", "[\"open\"]"),
            };
            await using var archive = new MemoryStream();
            await new AppPackageWriter().Write(descriptor, build, archive, Ct);
            archive.Position = 0;
            return await packages.Stage(archive, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Brain.DisposeAsync();
            packages.Dispose();
            foreach (var file in Directory.EnumerateFiles(home.Path, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
            home.Dispose();
        }
    }

    // Authority's membership view can change between review and activation. The concrete test authority deliberately
    // makes owner-role updates immutable, so this decorator changes only that read while retaining real calls otherwise.
    public class OwnershipView : DispatchProxy
    {
        public IIdentityClient Inner { get; set; } = null!;
        public bool Revoked { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Revoked && targetMethod?.Name == nameof(IIdentityClient.Membership)) { return Task.FromResult<Member?>(null); }
            return targetMethod!.Invoke(Inner, args);
        }
    }
}
