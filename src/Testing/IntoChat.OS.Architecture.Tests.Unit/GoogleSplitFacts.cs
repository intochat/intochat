using System.Reflection;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Google.Auth;
using IntoChat.OS.Modules.Google.Drive;
using IntoChat.OS.Modules.Google.Gmail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace IntoChat.OS.Architecture.Tests.Unit;

// D-07: Google is three modules. google-auth owns sign-in; Gmail and Drive each depend on it and never on each other.
public sealed class GoogleSplitFacts
{
    private static readonly Type[] Modules = [typeof(GoogleAuthModule), typeof(GmailModule), typeof(DriveModule)];

    [Fact]
    public void EachExternalOperationHasOneOwner()
    {
        var names = Modules.SelectMany(module => module.GetCustomAttributes<ExternalOperationAttribute>().Select(operation => operation.Name)).ToArray();
        Assert.Equal(15, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([GoogleAuthOperations.CodeExchange, GoogleAuthOperations.Refresh],
            typeof(GoogleAuthModule).GetCustomAttributes<ExternalOperationAttribute>().Select(operation => operation.Name).Order(StringComparer.Ordinal));
        Assert.All(typeof(DriveModule).GetCustomAttributes<ExternalOperationAttribute>(), operation => Assert.StartsWith("google.drive.", operation.Name, StringComparison.Ordinal));
        Assert.All(typeof(GmailModule).GetCustomAttributes<ExternalOperationAttribute>(), operation => Assert.StartsWith("google.gmail.", operation.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void DriveAndGmailDependOnAuthOnly()
    {
        static string[] References(Type module) => [.. module.Assembly.GetReferencedAssemblies().Select(name => name.Name!)];
        var auth = typeof(GoogleAuthModule).Assembly.GetName().Name;
        var gmail = typeof(GmailModule).Assembly.GetName().Name;
        var drive = typeof(DriveModule).Assembly.GetName().Name;
        Assert.Contains(auth, References(typeof(DriveModule)));
        Assert.DoesNotContain(gmail, References(typeof(DriveModule)));
        Assert.Contains(auth, References(typeof(GmailModule)));
        Assert.DoesNotContain(drive, References(typeof(GmailModule)));
        Assert.DoesNotContain(gmail, References(typeof(GoogleAuthModule)));
        Assert.DoesNotContain(drive, References(typeof(GoogleAuthModule)));
    }

    [Fact]
    public void OnlyAuthDeclaresGoogleOAuthIntegration()
    {
        var owners = Modules.Where(module => module.GetCustomAttributes<IntegrationAttribute>().Any(integration => integration.Id == GoogleAuth.Integration)).ToArray();
        Assert.Equal([typeof(GoogleAuthModule)], owners);
        Assert.Equal(GoogleAuth.Integration, GoogleAuthModule.Integration.Id);
        Assert.All(typeof(GoogleAuthModule).Assembly.GetExportedTypes().SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)),
            property => Assert.DoesNotContain("Token", property.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void DriveWithoutAuthFailsLoudly()
    {
        using var alone = Compose(new DriveModule());
        var error = Assert.Throws<InvalidOperationException>(() => GoogleAuth.EnsureRegistered(alone, DriveModule.ModuleName));
        Assert.Contains("google-auth", error.Message, StringComparison.Ordinal);

        using var gmailAlone = Compose(new GmailModule());
        Assert.Contains("google-auth", Assert.Throws<InvalidOperationException>(() => GoogleAuth.EnsureRegistered(gmailAlone, "gmail")).Message, StringComparison.Ordinal);

        // Order does not matter: Drive composed before Auth still passes.
        using var together = Compose(new DriveModule(), new GoogleAuthModule());
        GoogleAuth.EnsureRegistered(together, DriveModule.ModuleName);
    }

    private static ServiceProvider Compose(params IntoChat.OS.Framework.Modules.Module[] modules)
    {
        var silo = new Silo(new ServiceCollection(), new ConfigurationBuilder().Build());
        foreach (var module in modules) { module.Configure(silo); }
        return silo.Services.BuildServiceProvider();
    }

    private sealed record Silo(IServiceCollection Services, IConfiguration Configuration) : ISiloBuilder;
}
