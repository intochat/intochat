using IntoChat.OS.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class LegacyConfigurationGuardChallengerFacts
{
    private sealed class TestHost(string env) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = env;
        public string ApplicationName { get; set; } = "challenger";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void Guard_RejectsRetiredKeys_EvenWithEmptyOrWhitespaceValues()
    {
        var retiredKey = "IntoChatOS:Profile";
        var sensitiveValue = "SUPER_SECRET_VALUE_DO_NOT_LEAK";

        foreach (var testValue in new[] { "", "   ", sensitiveValue })
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [retiredKey] = testValue })
                .Build();

            foreach (var env in new[] { Environments.Development, Environments.Production })
            {
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    LegacyConfigurationGuard.Verify(config, new TestHost(env), [], null));

                Assert.Contains("Retired settings are not read", ex.Message, StringComparison.Ordinal);
                Assert.Contains(retiredKey, ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(sensitiveValue, ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Guard_RejectsRetiredKeysFromEnvironmentVariables_CaseInsensitive()
    {
        // Testing case-insensitive matching in environment variables
        var envVars = new[]
        {
            "INTOCHATOS__HOSTING__PERSISTENTSTORAGE",
            "intochatos__profile",
            "INTOCHATOS__INTEGRATIONS__ORLEANS__CLUSTERING"
        };

        var config = new ConfigurationBuilder().Build();

        foreach (var envVar in envVars)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LegacyConfigurationGuard.Verify(config, new TestHost(Environments.Production), [envVar], null));

            Assert.Contains(envVar, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Guard_DoesNotFalsePositiveOnSimilarPrefixKeys()
    {
        // "IntoChatOS:Profile" is retired, but "IntoChatOS:ProfileName" or "IntoChatOS:Profiles" should not be falsely rejected
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:ProfileName"] = "my-profile",
                ["IntoChatOS:Presets"] = "custom",
                ["IntoChatOS:Hosting:ModeOption"] = "custom-mode"
            })
            .Build();

        var logger = new RecordingLogger();
        // Should NOT throw for legitimate non-retired keys
        LegacyConfigurationGuard.Verify(config, new TestHost(Environments.Production), [], null, logger);
    }

    [Fact]
    public void Guard_MultipleRetiredKeys_ReportsAllOffendersInOneException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Profile"] = "val1",
                ["IntoChatOS:Preset"] = "val2",
                ["IntoChatOS:Hosting:Mode"] = "val3"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LegacyConfigurationGuard.Verify(config, new TestHost(Environments.Production), [], null));

        Assert.Contains("IntoChatOS:Profile", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IntoChatOS:Preset", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IntoChatOS:Hosting:Mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_LegacyProductNameKeys_WarnsInDev_ThrowsInProd()
    {
        var legacyKey = "DigitalBrain:SomeKey";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [legacyKey] = "some-val" })
            .Build();

        // In Development: logs warning, does NOT throw
        var devLogger = new RecordingLogger();
        LegacyConfigurationGuard.Verify(config, new TestHost(Environments.Development), [], null, devLogger);
        Assert.Single(devLogger.Messages);
        Assert.Contains("Configuration written for the previous product name", devLogger.Messages[0], StringComparison.Ordinal);

        // In Production: throws InvalidOperationException
        var prodEx = Assert.Throws<InvalidOperationException>(() =>
            LegacyConfigurationGuard.Verify(config, new TestHost(Environments.Production), [], null));
        Assert.Contains("Configuration written for the previous product name", prodEx.Message, StringComparison.Ordinal);
    }
}
