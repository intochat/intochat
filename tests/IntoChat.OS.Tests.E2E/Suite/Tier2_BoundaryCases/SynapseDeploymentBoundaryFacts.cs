using System.Reflection;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Tests.E2E.Harness;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using DigitalBrain.Capabilities.Secrets;

namespace IntoChat.OS.Tests.E2E.Tier2_BoundaryCases;

public sealed class SynapseDeploymentBoundaryFacts : E2ETestBase
{
    private static byte[] CompileAssembly(string sourceCode, bool includeSystemIo = false)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)
        };

        if (includeSystemIo)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(File).Assembly.Location));
        }

        var compilation = CSharpCompilation.Create(
            $"Unit_{Guid.NewGuid():N}",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        if (!result.Success)
        {
            var errors = string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Compilation failed: {errors}");
        }

        return peStream.ToArray();
    }

    [Fact]
    public async Task T2_Sources_Claim_AlreadyOwnedAlias_ReturnsAliasConflictRecord()
    {
        await using var os = TestOS.Local();
        os.Register<SourcesNeuron>();

        var brain = NewBrain("sources-conflict");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var sourcesId = new NeuronId(brain, "synapse.sources", "default");
        var sources = os.Get<ISources>(sourcesId);

        // Source A claims alias
        var claimA = await sources.Claim("source-A", ["chat.room"], TestContext.Current.CancellationToken);
        Assert.Null(claimA);

        // Source B attempts to claim same alias -> Conflict returned
        var conflict = await sources.Claim("source-B", ["chat.room"], TestContext.Current.CancellationToken);
        Assert.NotNull(conflict);
        Assert.Equal("chat.room", conflict.Alias);
        Assert.Equal("source-A", conflict.Owner);
    }

    [Fact]
    public async Task T2_Sources_Claim_NullArguments_ThrowsArgumentNullException()
    {
        await using var os = TestOS.Local();
        os.Register<SourcesNeuron>();

        var brain = NewBrain("sources-null-args");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var sourcesId = new NeuronId(brain, "synapse.sources", "default");
        var sources = os.Get<ISources>(sourcesId);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sources.Claim(null!, ["alias.one"], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sources.Claim("source-A", null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task T2_Sources_Release_AllowsSuccessorSourceToClaimWithoutConflict()
    {
        await using var os = TestOS.Local();
        os.Register<SourcesNeuron>();

        var brain = NewBrain("sources-release");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var sourcesId = new NeuronId(brain, "synapse.sources", "default");
        var sources = os.Get<ISources>(sourcesId);

        // Source A claims
        await sources.Claim("source-A", ["chat.room"], TestContext.Current.CancellationToken);
        Assert.Equal("source-A", await sources.OwnerOf("chat.room", TestContext.Current.CancellationToken));

        // Source A releases
        await sources.Release("source-A", ["chat.room"], TestContext.Current.CancellationToken);
        Assert.Null(await sources.OwnerOf("chat.room", TestContext.Current.CancellationToken));

        // Source B claims released alias -> succeeds with null conflict
        var claimB = await sources.Claim("source-B", ["chat.room"], TestContext.Current.CancellationToken);
        Assert.Null(claimB);
        Assert.Equal("source-B", await sources.OwnerOf("chat.room", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task T2_UnitDeployment_Propose_AliasConflict_ReturnsDeployOutcomeRefused()
    {
        await using var os = TestOS.Local();
        os.Register<SourcesNeuron>();
        os.Register<UnitDeployment>();

        var brain = NewBrain("deployment-conflict");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var sourcesId = new NeuronId(brain, "synapse.sources", "default");
        var sources = os.Get<ISources>(sourcesId);

        // Pre-claim alias by source-A
        await sources.Claim("source-A", ["shared.counter"], TestContext.Current.CancellationToken);

        // Source B proposes deployment declaring the already claimed alias
        var deploymentId = new NeuronId(brain, "synapse.deployment", "source-B");
        var deployment = os.Get<IUnitDeployment>(deploymentId);

        var dummyImage = new BuildImage(new BuildRef("source-B", "hash-b"), "UnitB", [0x01, 0x02], []);
        var build = new StoredBuild(
            dummyImage,
            new Dictionary<string, string>(),
            Icon: null,
            Aliases: ["shared.counter"],
            NeedsHashes: new Dictionary<string, string>());

        var outcome = await deployment.Propose(build, new Dictionary<string, IReadOnlyDictionary<string, string>>(), TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(DeployStatus.Refused, outcome.Status);
        Assert.Contains("already owned by source 'source-A'", outcome.Reason);
    }

    [Fact]
    public async Task T2_UnitLoader_Acquire_DisallowedAssemblyReference_ThrowsUnitRefusedException()
    {
        await using var os = TestOS.Local();

        // Compile assembly referencing System.IO.File (disallowed API outside sandbox)
        var maliciousSource = @"
public class MaliciousUnit
{
    public void Exploit()
    {
        System.IO.File.ReadAllText(""secret.txt"");
    }
}";
        var assemblyBytes = CompileAssembly(maliciousSource, includeSystemIo: true);
        var buildRef = new BuildRef("source-bad", "hash-bad-io");
        var image = new BuildImage(buildRef, "MaliciousUnitAssembly", assemblyBytes, []);

        var ex = Assert.Throws<UnitRefusedException>(() =>
            os.Loader.Acquire(image));

        Assert.Equal(buildRef, ex.Build);
        Assert.Contains("System.IO.File", ex.Disallowed);
    }

    [Fact]
    public async Task T2_UnitLoader_Acquire_CorruptedAssemblyBytes_ThrowsBadImageFormatException_AndUnloadsContext()
    {
        await using var os = TestOS.Local();

        var corruptBytes = new byte[] { 0x00, 0xFF, 0x42, 0x99, 0xAA };
        var buildRef = new BuildRef("source-corrupt", "hash-corrupt");
        var image = new BuildImage(buildRef, "CorruptAssembly", corruptBytes, []);

        Assert.Throws<BadImageFormatException>(() =>
            os.Loader.Acquire(image));

        // Unloaded context: nothing remains held in live loader table
        Assert.Null(os.Loader.TryAcquire(buildRef));
    }

    [Fact]
    public async Task T2_UnitLoader_RefcountZero_FiresUnloadingAndCollectsAlcCleanly()
    {
        await using var os = TestOS.Local();

        // Compile valid sandbox-compliant assembly
        var validSource = @"
public class ValidSandboxUnit
{
    public int Add(int a, int b) => a + b;
}";
        var assemblyBytes = CompileAssembly(validSource, includeSystemIo: false);
        var buildRef = new BuildRef("source-valid", "hash-valid");
        var image = new BuildImage(buildRef, "ValidSandboxUnitAssembly", assemblyBytes, []);

        bool unloadedFired = false;
        os.Loader.Unloading += (b, s) =>
        {
            if (b == buildRef)
            {
                unloadedFired = true;
            }
        };

        // Acquire handle
        var handle = os.Loader.Acquire(image);
        Assert.NotNull(handle);
        Assert.False(unloadedFired);

        // Dispose handle: refcount reaches 0 -> context is unloaded
        handle.Dispose();

        Assert.True(unloadedFired);
        Assert.Null(os.Loader.TryAcquire(buildRef));
    }
}
