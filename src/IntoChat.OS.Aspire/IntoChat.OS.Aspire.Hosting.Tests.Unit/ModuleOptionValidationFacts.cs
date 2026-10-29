using Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Validation;
using Orleans.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ModuleOptionValidationFacts
{
    [Theory]
    [InlineData("hosting-action")]
    [InlineData("hosting-value")]
    [InlineData("module-action")]
    public void AllEntryPathsRejectRequiredSettingsBeforeCustomValidation(string entry)
    {
        CheckedOptions? observed = null;
        var failure = Assert.Throws<ModuleValidationException>(() => Invoke(entry, options => observed = options));
        Assert.Equal(ModuleOptionsSerialization.OptionsKey("checked") + ":Endpoint", Assert.Single(failure.Issues).Path);
        Assert.Equal(0, Assert.IsType<CheckedOptions>(observed).ValidationCount);
    }

    [Theory]
    [InlineData("hosting-action")]
    [InlineData("hosting-value")]
    [InlineData("module-action")]
    public void AllEntryPathsRunCustomValidationOnceWithSafeNormalizedIssues(string entry)
    {
        CheckedOptions? observed = null;
        var failure = Assert.Throws<ModuleValidationException>(() => Invoke(entry, options =>
        {
            observed = options;
            options.Endpoint = new("https://example.test/secret-sentinel");
            options.Reject = true;
        }));
        Assert.Equal(ModuleOptionsSerialization.OptionsKey("checked") + ":Endpoint", Assert.Single(failure.Issues).Path);
        Assert.Equal(1, Assert.IsType<CheckedOptions>(observed).ValidationCount);
        Assert.DoesNotContain("secret-sentinel", failure.ToString());
        Assert.DoesNotContain("secret-sentinel", failure.Issues[0].Message);
    }

    private static void Invoke(string entry, Action<CheckedOptions> configure)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions());
        switch (entry)
        {
            case "hosting-action": brain.WithHosting<CheckedHosting, CheckedOptions>(configure); break;
            case "hosting-value":
                var options = new CheckedOptions();
                configure(options);
                brain.WithHosting<CheckedHosting, CheckedOptions>(options);
                break;
            case "module-action": brain.WithModule<CheckedModule, CheckedOptions>(configure); break;
            default: throw new ArgumentException("Unknown test entry.", nameof(entry));
        }
    }

    [DigitalBrain.Module("checked")]
    public sealed class CheckedModule : DigitalBrain.Module
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [DigitalBrain.Setting.Of<CheckedOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection("checked"))], []);
    }

    public sealed class CheckedHosting : ModuleHosting<CheckedModule>
    {
        public override void Configure(IntoChatOSBuilder brain) { }
    }

    public sealed class CheckedOptions : IModuleOptions
    {
        [Setting(Required = true)] public Uri? Endpoint { get; set; }
        public bool Reject { get; set; }
        public int ValidationCount { get; private set; }
        public void Validate()
        {
            ValidationCount++;
            if (Reject) { throw new ModuleValidationException([new("Endpoint", "endpoint", "Choose an allowed endpoint.")]); }
        }
    }
}
