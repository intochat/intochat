using System.Text.Json;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// One rule for "is this package a vendor SDK", reused by every later fence: deny prefixes win over allow prefixes,
// and anything neither denies nor allows is reported as unclassified so a new vendor cannot slip in unlisted.
public static class VendorRule
{
    public enum Verdict { Allowed, Denied, Unclassified }

    private static readonly string[] DenyPrefixes = ["Azure.", "Microsoft.AspNetCore", "Microsoft.Extensions.AI", "Google.", "Grpc."];
    private static readonly string[] DenyExact = ["MimeKit"];
    private static readonly string[] AllowPrefixes = ["Microsoft.Orleans.", "Microsoft.Extensions.", "System."];

    internal const string RuntimeFramework = "Microsoft.NETCore.App";

    internal static Verdict Classify(string package)
        => DenyExact.Contains(package, StringComparer.Ordinal) || DenyPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal)) ? Verdict.Denied
            : AllowPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal)) ? Verdict.Allowed
            : Verdict.Unclassified;
}

// The os Framework holds no vendor SDKs (the runtime-package fence, RT-04, is Phase 3 in digitalbrain). Today's
// offenders are pinned in FrameworkVendorBaseline.json; 02-06 drives the list to empty. The ratchet fails when the
// list grows and when an entry goes away without the baseline shrinking.
public sealed class FrameworkHasNoVendorSdksFacts
{
    internal const string BaselineFile = "FrameworkVendorBaseline.json";
    private const string Framework = "IntoChat.OS.Framework";

    private static readonly string[] VendorFolders = ["Platform", "Http", "Auth", "Types", "Surface", "Contracts", "Configuration", "Caching"];

    internal static Dictionary<string, string[]> Capture()
    {
        var project = Repository.ProjectNamed(Framework);
        var document = XDocument.Load(project.Path);
        var offenders = new List<string>();
        foreach (var package in Repository.PackageReferences(project.Path))
        {
            switch (VendorRule.Classify(package))
            {
                case VendorRule.Verdict.Denied: offenders.Add("package:" + package); break;
                case VendorRule.Verdict.Unclassified: offenders.Add("unclassified-package:" + package); break;
                case VendorRule.Verdict.Allowed: break;
            }
        }
        offenders.AddRange(document.Descendants("FrameworkReference").Select(reference => reference.Attribute("Include")?.Value).OfType<string>()
            .Where(name => name != VendorRule.RuntimeFramework).Select(name => "framework:" + name));
        offenders.AddRange(Repository.References(project.Path)
            .Where(name => name.StartsWith("IntoChat.OS.Authority", StringComparison.Ordinal) || name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
            .Select(name => "project:" + name));
        offenders.AddRange(Directory.EnumerateDirectories(project.Directory).Select(Path.GetFileName).OfType<string>()
            .Where(folder => VendorFolders.Contains(folder, StringComparer.Ordinal)).Select(folder => "folder:" + folder));
        return new Dictionary<string, string[]>(StringComparer.Ordinal) { [Framework] = CaptureWriter.Ordered(offenders) };
    }

    [Theory]
    [InlineData("Azure.Core", VendorRule.Verdict.Denied)]
    [InlineData("Microsoft.AspNetCore.Http", VendorRule.Verdict.Denied)]
    [InlineData("Microsoft.Extensions.AI.Abstractions", VendorRule.Verdict.Denied)]
    [InlineData("Google.Protobuf", VendorRule.Verdict.Denied)]
    [InlineData("Grpc.Net.Client", VendorRule.Verdict.Denied)]
    [InlineData("MimeKit", VendorRule.Verdict.Denied)]
    [InlineData("Microsoft.Orleans.Streaming", VendorRule.Verdict.Allowed)]
    [InlineData("Microsoft.Extensions.Hosting", VendorRule.Verdict.Allowed)]
    [InlineData("System.Security.Cryptography.ProtectedData", VendorRule.Verdict.Allowed)]
    [InlineData("Newtonsoft.Json", VendorRule.Verdict.Unclassified)]
    public void TheVendorRuleClassifiesPackages(string package, VendorRule.Verdict expected)
        => Assert.Equal(expected, VendorRule.Classify(package));

    [Fact]
    public void FrameworkVendorReachOnlyShrinks()
    {
        var baseline = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(CaptureWriter.Path(BaselineFile)))!;
        var current = Capture();
        var grew = current.SelectMany(project => project.Value.Except(baseline.GetValueOrDefault(project.Key) ?? [], StringComparer.Ordinal)
            .Select(offender => $"{project.Key}: {offender}")).Order(StringComparer.Ordinal).ToArray();
        Assert.True(grew.Length == 0, "The os Framework's vendor reach grew; keep vendor SDKs in modules:\n" + string.Join('\n', grew));
        var gone = baseline.SelectMany(project => project.Value.Except(current.GetValueOrDefault(project.Key) ?? [], StringComparer.Ordinal)
            .Select(offender => $"{project.Key}: {offender}")).Order(StringComparer.Ordinal).ToArray();
        Assert.True(gone.Length == 0, "Framework vendor offenders went away; regenerate with scripts/capture-all.ps1:\n" + string.Join('\n', gone));
    }

    [Fact(Explicit = true)]
    public void CaptureFrameworkVendorBaseline() => CaptureWriter.Write(BaselineFile, Capture());
}
