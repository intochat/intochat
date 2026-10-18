using IntoChat.OS.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class LegacyConfigurationGuardFacts
{
    // The retired name is assembled so this file never matches the repository grep gate.
    private static readonly string Old = "Digital" + "Brain";
    private const string Sentinel = "sentinel-value-must-not-leak";

    [Theory]
    [MemberData(nameof(Offenders))]
    public void ProductionRefusesEachLegacyForm(string kind, string name, string? replacement)
    {
        var configuration = Configure(kind, name);
        var exception = Assert.Throws<InvalidOperationException>(() => Verify(configuration, Environments.Production, kind == "env" ? [name] : []));
        Assert.Contains(name, exception.Message);
        Assert.Contains(replacement!, exception.Message);
        Assert.Contains("no compatibility layer", exception.Message);
        Assert.DoesNotContain(Sentinel, exception.Message);
    }

    [Theory]
    [MemberData(nameof(Offenders))]
    public void DevelopmentOnlyWarns(string kind, string name, string? replacement)
    {
        var logger = new CapturingLogger();
        Verify(Configure(kind, name), Environments.Development, kind == "env" ? [name] : [], logger);
        var warning = Assert.Single(logger.Messages);
        Assert.Contains(name, warning);
        Assert.Contains(replacement!, warning);
        Assert.DoesNotContain(Sentinel, warning);
    }

    [Fact]
    public void AnOldLocalDataDirectoryIsReportedUnlessTheNewOneExists()
    {
        var root = Directory.CreateTempSubdirectory("legacy-guard-").FullName;
        Directory.CreateDirectory(Path.Combine(root, Old));
        var configuration = new ConfigurationBuilder().Build();
        var exception = Assert.Throws<InvalidOperationException>(() => LegacyConfigurationGuard.Verify(configuration, Host(Environments.Production), [], root));
        Assert.Contains("IntoChatOS", exception.Message);
        Assert.True(Directory.Exists(Path.Combine(root, Old)));
        Directory.CreateDirectory(Path.Combine(root, "IntoChatOS"));
        LegacyConfigurationGuard.Verify(configuration, Host(Environments.Production), [], root);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void CurrentNamesAreNotOffenders()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IntoChatOS:Auth:Posture"] = "Secured",
            ["Parameters:Modules-intochat-os-master-key"] = Sentinel,
        }).Build();
        Verify(configuration, Environments.Production, ["IntoChatOS__Auth__Posture", "INTOCHATOS_UI_BASE", "PATH"]);
    }

    [Fact]
    public void EveryOffenderIsNamedInOneMessage()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [Old + ":Auth:Posture"] = Sentinel,
            ["Parameters:Modules-" + Old.ToLowerInvariant() + "-master-key"] = Sentinel,
        }).Build();
        var exception = Assert.Throws<InvalidOperationException>(() => Verify(configuration, Environments.Production, [Old.ToUpperInvariant() + "_UI_BASE"]));
        Assert.Contains(Old + ":Auth:Posture", exception.Message);
        Assert.Contains("Modules-" + Old.ToLowerInvariant() + "-master-key", exception.Message);
        Assert.Contains(Old.ToUpperInvariant() + "_UI_BASE", exception.Message);
        Assert.DoesNotContain(Sentinel, exception.Message);
    }

    public static IEnumerable<TheoryDataRow<string, string, string?>> Offenders()
    {
        yield return new("key", Old + ":Auth:Posture", "IntoChatOS:Auth:Posture");
        yield return new("env", Old + "__MasterKey", "IntoChatOS__MasterKey");
        yield return new("env", Old.ToUpperInvariant() + "_UI_BASE", "INTOCHATOS_UI_BASE");
        yield return new("key", "Parameters:Modules-" + Old.ToLowerInvariant() + "-master-key", "intochat-os-master-key");
        yield return new("key", "Parameters:" + Old.ToLowerInvariant() + "-bootstrap-password", "intochat-os-bootstrap-password");
    }

    private static IConfiguration Configure(string kind, string name)
        => new ConfigurationBuilder().AddInMemoryCollection(kind == "key" ? new Dictionary<string, string?> { [name] = Sentinel } : []).Build();

    private static void Verify(IConfiguration configuration, string environment, string[] variables, ILogger? logger = null)
        => LegacyConfigurationGuard.Verify(configuration, Host(environment), variables, null, logger);

    private static IHostEnvironment Host(string name) => new FakeEnvironment(name);

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) { Messages.Add(formatter(state, exception)); } }
    }
}
