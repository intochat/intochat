using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Apps.Tests.Integration;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Features;

// Built-ins reconcile by digest: a host that ships a different package for an app moves a person's unedited copy onto it (origin
// recorded first, then the prebuilt assigned); an edited copy is left alone and offered the update; a release that asks for more waits.
public sealed class BuiltinReconciliationFacts : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("builtin-reconcile-").FullName;
    private string Inventory => Path.Combine(_root, "compiled-apps");

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(_root, recursive: true);
    }

    private Task<ITestBrain> Start() => TestBrain.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.CompiledFeatures = Inventory)
        .WithModule<IntoChat.OS.Modules.Microsoft.CSharp.CSharpModule>()
        .WithFakeAppRuntime(new())
        .StartAsync(Ct);

    private static IBrainPrograms Programs(ITestBrain brain, Person person)
        => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IBrainPrograms>(BrainRuntimeKeys.For(person.BrainId));

    private static IFeatureReconciler Reconciler(ITestBrain brain, Person person) => brain.Get<IFeatureReconciler>(IFeatureReconciler.Key(person.BrainId));

    private static async Task<(Person Alice, IApp Copy)> Installed(ITestBrain brain)
    {
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller()) { await brain.InstallLatest("os", "counter", Ct); }
        return (alice, brain.Get<IApp>(IApp.Key(alice.BrainId, "counter")));
    }

    private static string HashOf(byte[] assembly) => Convert.ToHexStringLower(SHA256.HashData(assembly));

    private static async Task Reload(ITestBrain brain)
        => await brain.Services.GetRequiredService<FeatureCatalog>().Reload("os", "counter");

    [Trait("host", "own")]
    [Fact]
    public async Task AShippedProgramPackageInstallsAsItsPrebuiltAssembly()
    {
        var shipped = await Ship("1.0.0", marker: 1);
        await using var brain = await Start();

        var (alice, copy) = await Installed(brain);

        var snapshot = await copy.Read();
        Assert.False(snapshot.Edited);
        Assert.Equal(shipped.Digest, snapshot.Origin!.PackageDigest);
        Assert.Equal("// open\n", snapshot.Files["open.cs"]);
        Assert.Equal(HashOf(shipped.Assembly), Assert.Single((await Programs(brain, alice).Describe(IApp.Key(alice.BrainId, "counter")))!.Behaviors).Hash);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUneditedCopyMovesToADifferentShippedDigestAtTheSameVersion()
    {
        await Ship("1.0.0", marker: 1);
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);

        var next = await Ship("1.0.0", marker: 2);
        await Reload(brain);
        using (alice.AsCaller()) { await Reconciler(brain, alice).Reconcile(Ct); }

        var snapshot = await copy.Read();
        Assert.Equal(next.Digest, snapshot.Origin!.PackageDigest);
        Assert.False(snapshot.Edited);
        Assert.Equal(HashOf(next.Assembly), Assert.Single((await Programs(brain, alice).Describe(IApp.Key(alice.BrainId, "counter")))!.Behaviors).Hash);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEditedCopyIsNeverOverwrittenAndIsOfferedTheShippedUpdate()
    {
        var first = await Ship("1.0.0", marker: 1);
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);
        using (alice.AsCaller()) { await copy.WriteBehavior("open.cs", "// mine\n", Guid.NewGuid()); }

        await Ship("1.1.0", marker: 2);
        await Reload(brain);
        using (alice.AsCaller()) { await Reconciler(brain, alice).Reconcile(Ct); }

        var snapshot = await copy.Read();
        Assert.True(snapshot.Edited);
        Assert.Equal("1.0.1", snapshot.Version);
        Assert.Equal("// mine\n", snapshot.Files["open.cs"]);
        Assert.Equal(first.Digest, snapshot.Origin!.PackageDigest);
        var offered = await brain.Services.GetRequiredService<ShippedPrograms>().DigestOf("os", "counter", Ct);
        Assert.NotEqual(snapshot.Origin.PackageDigest, offered);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReleaseThatAsksForMoreWaitsForConsent()
    {
        var first = await Ship("1.0.0", marker: 1);
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);

        await Ship("1.1.0", marker: 2, requires: "new(Secrets: [\"extra.key\"])");
        await Reload(brain);
        using (alice.AsCaller()) { await Reconciler(brain, alice).Reconcile(Ct); }

        var snapshot = await copy.Read();
        Assert.Equal("1.0.0", snapshot.Version);
        Assert.Equal(first.Digest, snapshot.Origin!.PackageDigest);
    }

    private sealed record Shipped(string Digest, byte[] Assembly);

    // A one-synapse program package for os/counter: the assembly differs by marker, so the digest does.
    private async Task<Shipped> Ship(string version, int marker, string? requires = null)
    {
        var build = Directory.CreateDirectory(Path.Combine(_root, "build", Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(build, "files"));
        Directory.CreateDirectory(Path.Combine(build, "assemblies"));
        var assembly = await File.ReadAllBytesAsync(typeof(BuiltinReconciliationFacts).Assembly.Location, Ct);
        assembly = [.. assembly, .. new byte[marker]];
        await File.WriteAllBytesAsync(Path.Combine(build, "assemblies", "open.dll"), assembly, Ct);
        await File.WriteAllTextAsync(Path.Combine(build, "files", "open.cs.src"), "// open\n", Ct);
        var requirement = requires is null ? "" : ",\n        Requires: " + requires;
        await File.WriteAllTextAsync(Path.Combine(build, "files", "app.cs.src"), $$"""
            using IntoChat.OS.Modules.Apps;

            static class App
            {
                public const string Name = "counter";
                public const string Version = "{{version}}";

                public static readonly AppManifest Manifest = new(
                    Title: "Counter",
                    Description: "Counts",
                    Operations: [new("open", "Open")],
                    Settings: [],
                    Slots: []{{requirement}});
            }
            """, Ct);
        var descriptor = new CompiledAppDescriptor
        {
            Id = "os/counter",
            Name = "Counter",
            Version = version,
            EntryAssembly = "assemblies/open.dll",
            EntryType = "Program",
            HostMetadata = ImmutableSortedDictionary<string, string>.Empty.Add("kind", "program")
                .Add("synapses", JsonSerializer.Serialize(new[] { new { source = "files/open.cs.src", assembly = "assemblies/open.dll" } })),
        };
        Directory.CreateDirectory(Path.Combine(Inventory, "os"));
        var path = Path.Combine(Inventory, "os", "counter.intochat-app");
        await using var archive = new MemoryStream();
        await new AppPackageWriter().Write(descriptor, build, archive, Ct);
        archive.Position = 0;
        await using (var file = File.Create(path)) { await archive.CopyToAsync(file, Ct); }
        return new(Convert.ToHexStringLower(SHA256.HashData(archive.ToArray())), assembly);
    }
}
