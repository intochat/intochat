using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests.Unit;

// One caller: DigitalBrain.Access.Caller is the only identity a call carries. The old models stay deleted, no rule reads a
// request-supplied identity, no neuron contract or port takes a caller as an argument, and the kernel scope is entered only by host code.
public sealed class OneCallerFacts
{
    private static readonly string[] OldCallerTypes =
        ["CapabilityCaller", "CapabilityCallers", "CallerKind", "TrustedEdge", "RuntimeCallerContext", "LegacyCallers", "AccessCounters"];

    // Files under framework/src (relative to it) that may mint or enter a kernel-level caller, by call shape.
    private static readonly (string Call, string[] Sites)[] KernelEntries =
    [
        ("EnterKernel(",
        [
            "DigitalBrain/Access/EdgeAuthority.cs",                               // the definition
            "DigitalBrain/Identity/Local/Vault/LocalVaultPort.cs",                // platform port: own owner check first, then reads the vault as platform code; no edge, no scope, vault refuses
            "DigitalBrain.Testing.Silo/Conformance/Host/HostConformanceFacts.cs", // test host harness acting as the platform
            "DigitalBrain.Simulation/Synapses/UnitScenario.cs",                   // scenario harness feeding its cluster-internal publisher
        ]),
        ("EnterKernelScope(",
        [
            "DigitalBrain/Access/EdgeAuthority.cs",                               // EnterInProcess and ClearCaller, the host's in-process scopes
            "DigitalBrain/Access/ICallerAmbient.cs",                              // the ambient contract
            "DigitalBrain.Runtime.Orleans/Access/OrleansCallerAmbient.cs",        // Orleans ambient implementation
            "DigitalBrain.Simulation/TestOS/TestCallerAmbient.cs",                // TestOS ambient implementation
        ]),
        ("Caller.OfKernel(",
        [
            "DigitalBrain/Access/EdgeAuthority.cs",                               // EnterKernel and the Kernel minting method on the host-only edge
            "DigitalBrain.Runtime.Orleans/Access/KernelHop.cs",                   // runtime hop into the kernel, opens its own trace
            "DigitalBrain.Runtime.Orleans/Capabilities/PlatformIdentityPorts.cs", // platform reaching its own identity store as the system brain kernel
            "DigitalBrain.Runtime.Orleans/Hosting/OrleansInvocationSink.cs",      // only when the invocation is explicitly flagged asKernel
            "DigitalBrain.Runtime.Orleans/Synapses/OrleansInstallHooks.cs",       // synapse install runs as the kernel, via the installs neuron
        ]),
    ];

    // The facts read framework/src as text, so they cover every project there, including the runtime and host projects the unit tier must not reference.
    [Fact]
    public void NoOldCallerTypeExists()
    {
        var declaration = new Regex(@"\b(?:class|struct|enum|interface|record(?:\s+(?:class|struct))?|delegate\s+[\w<>?\[\],.]+)\s+(" + string.Join("|", OldCallerTypes) + @")\b");
        var found = FrameworkSources().SelectMany(path => declaration.Matches(File.ReadAllText(path)).Select(match => $"{match.Groups[1].Value} in {path}")).ToList();
        Assert.True(found.Count == 0, string.Join(", ", found));
    }

    [Fact]
    public void NoRequestStringIdentity()
    {
        var offenders = FrameworkSources().Where(path => File.ReadAllText(path).Contains("\"db.cap.", StringComparison.Ordinal)).ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void NoInterfaceMethodTakesACaller()
    {
        var parameter = new Regex(@"\b(?:DigitalBrain\.Access\.)?Caller\??\s+\w+\s*[,)]");
        var offenders = FrameworkSources()
            .SelectMany(path => PublicInterfaceBodies(File.ReadAllText(path)).Where(body => parameter.IsMatch(body.Text)).Select(body => $"{body.Name} in {path}"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void EnterKernelOnlyAtAllowedSites()
    {
        var root = Path.Combine(Repository.Root, "src");
        foreach (var (call, sites) in KernelEntries)
        {
            var allowed = sites.Select(site => Path.GetFullPath(Path.Combine(root, site))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var offenders = FrameworkSources()
                .Where(path => File.ReadAllText(path).Contains(call, StringComparison.Ordinal))
                .Where(path => !allowed.Contains(Path.GetFullPath(path)))
                .ToList();
            Assert.True(offenders.Count == 0, $"{call}: {string.Join(", ", offenders)}");
            Assert.All(allowed, site => Assert.True(File.Exists(site), $"{site} is on the {call} allowlist but missing."));
        }
    }

    private static IEnumerable<string> FrameworkSources()
        => Repository.SourceFiles(Path.Combine(Repository.Root, "src"), "*.cs");

    private static IEnumerable<(string Name, string Text)> PublicInterfaceBodies(string source)
    {
        foreach (Match match in Regex.Matches(source, @"\bpublic\s+(?:partial\s+)?interface\s+(\w+)[^{;]*\{"))
        {
            var depth = 1;
            var index = match.Index + match.Length;
            var start = index;
            while (index < source.Length && depth > 0)
            {
                depth += source[index] == '{' ? 1 : source[index] == '}' ? -1 : 0;
                index++;
            }
            yield return (match.Groups[1].Value, source[start..index]);
        }
    }
}
