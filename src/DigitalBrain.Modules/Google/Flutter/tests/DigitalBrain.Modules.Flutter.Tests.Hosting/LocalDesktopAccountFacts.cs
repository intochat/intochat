using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Aspire.Testing;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Aspire.Hosting;

namespace DigitalBrain.Modules.Flutter.Tests.Hosting;

public sealed class LocalDesktopAccountFacts
{
    [Theory]
    [InlineData(false, false, "Development", true)]
    [InlineData(false, true, "Development", false)]
    [InlineData(true, false, "Development", false)]
    [InlineData(false, false, "Production", false)]
    public async Task OnlyTheLocalDesktopExecutableReceivesThePrivateFilePath(bool publish, bool web, string environment, bool expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var builder = publish ? AspireCompositionTest.CreatePublishBuilder() : AspireCompositionTest.CreateBuilder();
        builder.Environment.EnvironmentName = environment;
        builder.Configuration[LocalDesktopAccount.ConfigurationKey] = "vlad";
        builder.AddDigitalBrain("local-account-facts", new DigitalBrainHostingOptions { PersistentStorage = false })
            .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter =>
            {
                if (web) { flutter.RunWebApp(); } else { flutter.RunDesktopApp(); }
            });
        var shell = Assert.Single(builder.Resources.OfType<ExecutableResource>(), resource => resource.Name.Contains("FlutterShell", StringComparison.Ordinal));
        var values = await AspireCompositionTest.EnvironmentOf(builder, shell, TestContext.Current.CancellationToken);
        Assert.Equal(expected, values.ContainsKey(LocalDesktopAccount.FileEnvironmentVariable));
        if (expected)
        {
            var path = Assert.IsType<string>(values[LocalDesktopAccount.FileEnvironmentVariable]);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var secret = json.RootElement.GetProperty("password").GetString()!;
            Assert.DoesNotContain(values.Values, value => value.ToString()!.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RestartReusesTheSamePrivateCredential()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(Path.GetTempPath(), "digitalbrain-local-account-" + Guid.NewGuid().ToString("N"));
        var path = LocalDesktopAccount.Ensure(directory, "vlad");
        var before = File.ReadAllText(path);
        Assert.Equal(path, LocalDesktopAccount.Ensure(directory, "vlad"));
        Assert.Equal(before, File.ReadAllText(path));
        using var json = JsonDocument.Parse(before);
        Assert.Equal("vlad", json.RootElement.GetProperty("principalId").GetString());
        Assert.Equal(64, json.RootElement.GetProperty("password").GetString()!.Length);
        var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        using var user = WindowsIdentity.GetCurrent();
        var allowed = new[] { user.User!.Value, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
        foreach (var rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
        { Assert.Contains(rule.IdentityReference.Value, allowed); }
        File.Delete(path);
        Directory.Delete(directory);
    }

    [Fact]
    public void AnExistingMalformedCredentialIsNotReplaced()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(Path.GetTempPath(), "digitalbrain-local-account-" + Guid.NewGuid().ToString("N"));
        var path = LocalDesktopAccount.Ensure(directory, "vlad");
        File.WriteAllText(path, "invalid-json");
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (OperatingSystem.IsWindows()) { LocalDesktopAccount.Ensure(directory, "vlad"); }
        });
        Assert.Equal("invalid-json", File.ReadAllText(path));
        File.Delete(path);
        Directory.Delete(directory);
    }

    [Theory]
    [InlineData(true, true, true, true, "vlad", true)]
    [InlineData(false, true, true, true, "vlad", false)]
    [InlineData(true, false, true, true, "vlad", false)]
    [InlineData(true, true, false, true, "vlad", false)]
    [InlineData(true, true, true, false, "vlad", false)]
    [InlineData(true, true, true, true, null, false)]
    public void OnlyAnExplicitLocalWindowsDesktopRunProvisionsAnAccount(
        bool run, bool development, bool desktop, bool windows, string? principal, bool expected)
        => Assert.Equal(expected, LocalDesktopAccount.Enabled(run, development, desktop, windows, principal));
}
