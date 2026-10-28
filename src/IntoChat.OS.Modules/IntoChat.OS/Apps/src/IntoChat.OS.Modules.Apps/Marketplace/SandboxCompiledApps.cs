using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// Executes an owner-authorized package: its verified assemblies are read from the package cache and assigned to the brain's runtime, which
// stores them by hash and has the sandbox load exactly those bytes. Authorization (owner trust, review, consent, bindings) is complete before
// this is called; nothing here widens it. The digest that is running is kept in the package cache's active record, so the installation is
// known after a restart and a development lease can be matched against it.
internal sealed class SandboxCompiledApps(IServiceProvider plain, CompiledAppInstaller installer,
    ActiveCompiledApps active) : ICompiledAppAssignment
{
    public async Task<CompiledAppInstallation?> Current(string brain, string name, AppSnapshot snapshot, CancellationToken ct)
    {
        var record = active.Read(brain, name);
        if (record is null) { return null; }
        var program = await Programs(brain).Describe(IApp.Key(brain, name)).WaitAsync(ct);
        return new(record.Digest, record.Revision, record.OperationId, program is not null, record.Receipt);
    }

    public async Task<AppSnapshot> Assign(AuthorizedCompiledApp app, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        var receipt = app.Receipt;
        var key = IApp.Key(receipt.Brain, receipt.Name);
        var target = SiloNeurons.Get<IApp>(plain, key);
        var snapshot = await target.Read().WaitAsync(ct);
        if (snapshot.ProgramRevision != receipt.ExpectedRevision)
        { throw new InvalidOperationException("The app changed after it was reviewed; review the package again."); }
        var behaviors = await Behaviors(app.Package, ct);
        // Grants first, idempotent: an app that cannot be granted what it asked for is never started.
        await installer.ApplyGrants(receipt, ct);
        snapshot = snapshot.Manifest.Title.Length == 0
            ? await target.Create(app.Manifest, app.Package.Descriptor.Presentation.GetValueOrDefault("spec", "")).WaitAsync(ct)
            : await target.SaveManifest(app.Manifest, receipt.OperationId).WaitAsync(ct);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App"] = key,
            ["ProgramRevision"] = snapshot.ProgramRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Digest"] = app.Package.Digest,
        };
        await PrebuiltPrograms.Assign(Programs(receipt.Brain), key, behaviors, settings, app.Package.Digest, ct);
        active.Write(receipt.Brain, receipt.Name, new(app.Package.Digest, snapshot.ProgramRevision, receipt.OperationId, receipt));
        return await target.Read().WaitAsync(ct);
    }

    private IBrainPrograms Programs(string brain) => SiloNeurons.Get<IBrainPrograms>(plain, BrainRuntimeKeys.For(brain));

    // Every assembly of the package that is a behavior. A built-in program package lists its synapses; any other package is its one entry assembly.
    // Nothing is loaded or reflected here: the bytes are read and hashed, and the sandbox is what runs them.
    internal static async Task<Dictionary<string, PrebuiltBehavior>> Behaviors(ValidatedAppPackage package, CancellationToken ct)
    {
        var descriptor = package.Descriptor;
        (string Path, string Assembly)[] assemblies = descriptor.HostMetadata.GetValueOrDefault("kind") == "program"
            ? [.. JsonSerializer.Deserialize<AssemblyEntry[]>(descriptor.HostMetadata["synapses"], SynapseJson)!
                .Select(synapse => (BehaviorPath(synapse.Source), synapse.Assembly))]
            : [(System.IO.Path.GetFileName(descriptor.EntryAssembly), descriptor.EntryAssembly)];
        var result = new Dictionary<string, PrebuiltBehavior>(StringComparer.Ordinal);
        foreach (var (path, assembly) in assemblies)
        {
            var bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(package.DirectoryPath, assembly.Replace('/', System.IO.Path.DirectorySeparatorChar)), ct);
            result[path] = new(bytes, [], Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        return result;
    }

    // "files/add.cs.src" is the program file "add.cs".
    private static string BehaviorPath(string source)
    {
        var path = source.StartsWith("files/", StringComparison.Ordinal) ? source["files/".Length..] : source;
        return path.EndsWith(".src", StringComparison.Ordinal) ? path[..^".src".Length] : path;
    }

    // The packer writes camelCase names.
    private static readonly JsonSerializerOptions SynapseJson = new(JsonSerializerDefaults.Web);

    private sealed record AssemblyEntry(string Source, string Assembly);
}

internal sealed record ActiveCompiledApp(string Digest, long Revision, Guid OperationId, CompiledAppTrustReceipt Receipt);

// The digest each compiled app runs, one small file per app beside the package archives; replaced whole so a crash leaves the old or the new.
internal sealed class ActiveCompiledApps
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;

    internal ActiveCompiledApps(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    internal ActiveCompiledApp? Read(string brain, string name)
    {
        var path = PathOf(brain, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<ActiveCompiledApp>(File.ReadAllText(path), Json) : null;
    }

    internal void Write(string brain, string name, ActiveCompiledApp record)
    {
        var path = PathOf(brain, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private string PathOf(string brain, string name)
        => Path.Combine(_root, Uri.EscapeDataString(brain), Uri.EscapeDataString(name) + ".json");
}
