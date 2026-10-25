using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;
using IntoChat.OS.Testing;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Framework.Tests.Integration.Runtime.Hosting;

public sealed class StateHomeFacts
{
    private static IConfiguration With(string? home) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [IntoChatOSNames.HomeKey] = home }).Build();

    [Fact]
    public void AConfiguredAbsoluteHomeIsUsed()
    {
        using var home = TestHome.Create();
        var resolved = StateHome.Resolve(With(home.Path));
        Assert.Equal(Path.GetFullPath(home.Path), resolved.Path);
        Assert.Equal(Path.Combine(resolved.Path, "state"), resolved.State);
        Assert.Equal(Path.Combine(resolved.Path, "secrets"), resolved.Secrets);
    }

    [Fact]
    public void AnUnsetHomeIsTheUserProfileIntochatDirectory()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".intochat");
        Assert.Equal(expected, StateHome.Resolve(With(null)).Path);
        Assert.Equal(expected, StateHome.Resolve(null).Path);
        Assert.Equal(Path.Combine(expected, "state"), StateHome.Resolve(With("")).State);
    }

    [Fact]
    public void ARelativeHomeIsRefusedNamingTheKey()
    {
        var error = Assert.Throws<InvalidOperationException>(() => StateHome.Resolve(With("relative/home")));
        Assert.Contains(IntoChatOSNames.HomeKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("relative/home", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsAreCreatedOwnerOnly()
    {
        using var home = TestHome.Create();
        var secrets = StateHome.Resolve(With(home.Path)).CreateSecrets();
        Assert.True(Directory.Exists(secrets));
        if (OperatingSystem.IsWindows()) { AssertOwnerOnlyAcl(secrets); }
        else { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(secrets)); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyAcl(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        { Assert.Equal(user, rule.IdentityReference); }
        Assert.True(security.AreAccessRulesProtected);
    }

    [Fact]
    public void ATestHomeIsATemporaryDirectoryDeletedOnDispose()
    {
        var home = TestHome.Create();
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), home.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IntoChatOSNames.HomeKey, home.Setting.Key);
        Assert.True(Directory.Exists(home.Path));
        home.Dispose();
        Assert.False(Directory.Exists(home.Path));
    }
}
