using DigitalBrain;
using DigitalBrain.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class SideloadConsentChallengerFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sideload-challenger-").FullName;
    private readonly List<string> _installedCalls = [];

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SideloadInstaller CreateInstaller()
    {
        var git = new GitSideload((_, _) => Task.FromResult(0), GitSideload.MaxBytes);
        var catalog = new FeatureCatalog(directory: null, new PassGate(), dev: null, sideload: new SideloadDirectory(_root), git: git);
        return new SideloadInstaller(catalog, git, (request, _) =>
        {
            _installedCalls.Add(request.Ref.Publisher + "/" + request.Ref.App);
            return Task.FromResult(InstallOutcome.Installed(request.Ref.App));
        });
    }

    private void WriteAppBundle(string name, AppRequirements? requires = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name));
        var manifest = new AppManifest(
            Title: name,
            Description: "Challenger test app",
            Operations: [],
            Settings: [],
            Slots: [],
            Requires: requires);
        File.WriteAllText(Path.Combine(folder.FullName, "app.cs"), AppDeclaration.Write(new(name, "1.0.0", manifest, "")));
        File.WriteAllText(Path.Combine(folder.FullName, "main.cs"), "static class App { }\n");
    }

    [Fact]
    public async Task Sideload_AppRequiringCapabilities_RejectedWhenConsentIsNullOrWhitespaceOrGarbage()
    {
        var ct = TestContext.Current.CancellationToken;
        var requires = new AppRequirements(SemanticTypes: ["person.profile"]);
        WriteAppBundle("secure-app", requires);
        var installer = CreateInstaller();

        // 1. Consent is null
        var resultNull = await installer.Install("secure-app", consent: null, ct);
        Assert.False(resultNull.Ok);
        Assert.Contains("Consent required", resultNull.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);

        // 2. Consent is empty string
        var resultEmpty = await installer.Install("secure-app", consent: "", ct);
        Assert.False(resultEmpty.Ok);
        Assert.Contains("Consent required", resultEmpty.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);

        // 3. Consent is whitespace
        var resultWhitespace = await installer.Install("secure-app", consent: "   ", ct);
        Assert.False(resultWhitespace.Ok);
        Assert.Contains("Consent required", resultWhitespace.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);

        // 4. Consent is arbitrary garbage
        var resultGarbage = await installer.Install("secure-app", consent: "not-a-valid-sha256-hash", ct);
        Assert.False(resultGarbage.Ok);
        Assert.Contains("Consent required", resultGarbage.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);
    }

    [Fact]
    public async Task Sideload_AppRequiringCapabilities_EnforcesStrictLowercaseHexOrdinalConsent()
    {
        var ct = TestContext.Current.CancellationToken;
        var requires = new AppRequirements(SemanticTypes: ["person.profile"]);
        WriteAppBundle("case-test-app", requires);
        var installer = CreateInstaller();

        var validLower = AppConsent.Of(requires);
        var upperCase = validLower.ToUpperInvariant();

        // Uppercase hex should be rejected because AppConsent.Of produces lowercase hex and compares ordinal
        var resultUpper = await installer.Install("case-test-app", consent: upperCase, ct);
        Assert.False(resultUpper.Ok);
        Assert.Contains("Consent required", resultUpper.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);

        // Trailing whitespace rejected
        var resultPadded = await installer.Install("case-test-app", consent: validLower + " ", ct);
        Assert.False(resultPadded.Ok);
        Assert.Contains("Consent required", resultPadded.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);

        // Exact lowercase match succeeds
        var resultValid = await installer.Install("case-test-app", consent: validLower, ct);
        Assert.True(resultValid.Ok);
        Assert.Equal("Installed case-test-app.", resultValid.Message);
        Assert.Single(_installedCalls, "sideload/case-test-app");
    }

    [Fact]
    public async Task Sideload_AppWithEmptyOrNullRequirements_SucceedsWithoutConsent()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteAppBundle("empty-req-app", new AppRequirements([], [], [], []));
        WriteAppBundle("null-req-app", null);
        var installer = CreateInstaller();

        var resultEmpty = await installer.Install("empty-req-app", consent: null, ct);
        Assert.True(resultEmpty.Ok);
        Assert.Equal("Installed empty-req-app.", resultEmpty.Message);

        var resultNull = await installer.Install("null-req-app", consent: null, ct);
        Assert.True(resultNull.Ok);
        Assert.Equal("Installed null-req-app.", resultNull.Message);

        Assert.Equal(["sideload/empty-req-app", "sideload/null-req-app"], _installedCalls);
    }

    [Fact]
    public async Task Sideload_MultiDimensionalRequirements_RejectsPartialConsent()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullRequires = new AppRequirements(
            SemanticTypes: ["repo.read", "user.profile"],
            Connectors: ["github", "slack"],
            Secrets: ["api-token"],
            Capabilities: ["storage@1"]);
        WriteAppBundle("multi-req-app", fullRequires);
        var installer = CreateInstaller();

        // Attacker creates consent for only a subset (e.g. only SemanticTypes)
        var partialRequires = new AppRequirements(SemanticTypes: ["repo.read", "user.profile"]);
        var partialConsent = AppConsent.Of(partialRequires);

        var resultPartial = await installer.Install("multi-req-app", consent: partialConsent, ct);
        Assert.False(resultPartial.Ok);
        Assert.Contains("Consent required", resultPartial.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);

        // Full consent succeeds
        var fullConsent = AppConsent.Of(fullRequires);
        var resultFull = await installer.Install("multi-req-app", consent: fullConsent, ct);
        Assert.True(resultFull.Ok);
        Assert.Equal("Installed multi-req-app.", resultFull.Message);
        Assert.Single(_installedCalls, "sideload/multi-req-app");
    }

    [Fact]
    public async Task Sideload_TOCTOU_RequirementTamperingBetweenConsentAndInstallIsBlocked()
    {
        var ct = TestContext.Current.CancellationToken;
        var initialRequires = new AppRequirements(SemanticTypes: ["notes.read"]);
        WriteAppBundle("tampered-app", initialRequires);
        var installer = CreateInstaller();

        var initialConsent = AppConsent.Of(initialRequires);

        // Maliciously tamper with bundle requirements before install is executed
        var escalatedRequires = new AppRequirements(
            SemanticTypes: ["notes.read", "credentials.export"],
            Secrets: ["master-vault-key"]);
        WriteAppBundle("tampered-app", escalatedRequires);

        // Installing with the previously obtained consent must fail!
        var resultTampered = await installer.Install("tampered-app", consent: initialConsent, ct);
        Assert.False(resultTampered.Ok);
        Assert.Contains("Consent required", resultTampered.Message, StringComparison.Ordinal);
        Assert.Empty(_installedCalls);

        // Installing with updated matching consent succeeds
        var updatedConsent = AppConsent.Of(escalatedRequires);
        var resultUpdated = await installer.Install("tampered-app", consent: updatedConsent, ct);
        Assert.True(resultUpdated.Ok);
        Assert.Equal("Installed tampered-app.", resultUpdated.Message);
        Assert.Single(_installedCalls, "sideload/tampered-app");
    }

    [Fact]
    public async Task SideloadNeuron_PropagatesConsentProperly()
    {
        var requires = new AppRequirements(SemanticTypes: ["camera.access"]);
        WriteAppBundle("camera-app", requires);
        var installer = CreateInstaller();
        var services = new ServiceCollection()
            .AddSingleton(installer)
            .BuildServiceProvider();
        await using var os = DigitalBrain.Testing.TestOS.Local(o => o.Services = services);
        os.Register<SideloadNeuron>();
        var neuron = os.Get<ISideload>(new NeuronId(new BrainId("local"), "apps.sideload", "sideload"));

        // 1. SideloadNeuron install without consent -> blocked
        var denied = await neuron.Install("camera-app", consent: null);
        Assert.False(denied.Ok);
        Assert.Equal("Consent required: camera-app requests capabilities that require human approval.", denied.Message);
        Assert.Empty(_installedCalls);

        // 2. SideloadNeuron install with valid consent -> succeeds
        var validConsent = AppConsent.Of(requires);
        var allowed = await neuron.Install("camera-app", consent: validConsent);
        Assert.True(allowed.Ok);
        Assert.Equal("Installed camera-app.", allowed.Message);
        Assert.Single(_installedCalls, "sideload/camera-app");
    }

    private sealed class PassGate : IScriptSandbox
    {
        public bool CanRun => true;
        public string AuthoringDescription => "";
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, object signal, CancellationToken cancellationToken) => Task.FromResult<Verdict[]>([]);
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptCompilationCheck(true, []));
    }
}
