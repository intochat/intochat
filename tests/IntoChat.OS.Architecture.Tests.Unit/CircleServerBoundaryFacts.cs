namespace IntoChat.OS.Architecture.Tests.Unit;

// D-01, CLOUD-02, R4-01: the circle server module is built on DigitalBrain and contracts only, so anyone can run a
// circle from this repository without the os kernel's internals. Until the IntoChat.Cloud.* boundary fact (09-05)
// covers every cloud module, this one holds the line for the circle server.
public sealed class CircleServerBoundaryFacts
{
    private const string Module = "IntoChat.Cloud";
    private static readonly string[] AllowedProjects =
    [
        "IntoChat.OS",
        "IntoChat.OS.Framework",
        "IntoChat.OS.Modules.Circles",
    ];
    private static readonly string[] ThirdPartyPackages =
    [
        "Azure.Communication.Email",
        "Azure.Storage.Blobs",
        "Microsoft.AspNetCore.Authentication.Google",
        "Microsoft.Orleans.Sdk",
        "Otp.NET",
        "QRCoder",
    ];

    [Fact]
    public void TheCircleServerReferencesOnlyDigitalBrainAndContracts()
    {
        var project = Repository.ProjectNamed(Module).Path;
        var projects = Repository.References(project)
            .Where(reference => !reference.EndsWith(".Contracts", StringComparison.Ordinal)
                && !reference.StartsWith("DigitalBrain", StringComparison.Ordinal)
                && !AllowedProjects.Contains(reference))
            .ToArray();
        var packages = Repository.PackageReferences(project)
            .Where(package => !package.StartsWith("DigitalBrain", StringComparison.Ordinal) && !ThirdPartyPackages.Contains(package))
            .ToArray();
        Assert.True(projects.Length == 0, "Non-contract project references:\n" + string.Join('\n', projects));
        Assert.True(packages.Length == 0, "Packages outside DigitalBrain and the allowed third-party set:\n" + string.Join('\n', packages));
    }

    [Fact]
    public void TheCircleServerNeverReusesRuntimeMachineAuthentication()
    {
        var offenders = Repository.SourceFiles(Repository.ProjectNamed(Module).Directory, "*.cs")
            .Where(file => File.ReadAllText(file).Contains("RuntimeMachineAuthentication", StringComparison.Ordinal))
            .Select(Repository.Relative).ToArray();
        Assert.True(offenders.Length == 0, "D-09: the machine scheme is its own:\n" + string.Join('\n', offenders));
    }
}
