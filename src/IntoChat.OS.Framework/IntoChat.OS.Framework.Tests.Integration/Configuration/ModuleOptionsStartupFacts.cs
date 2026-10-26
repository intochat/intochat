using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Connections;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class ModuleOptionsStartupFacts
{
    [Fact]
    public async Task InvalidModuleOptionsRejectHostStartupWithoutFirstResolution()
    {
        var builder = Builder();
        CheckedOptions? observed = null;
        builder.Services.PostConfigure<CheckedOptions>(options => observed = options);
        using var host = builder.Build();
        var failure = await Assert.ThrowsAsync<ModuleValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ModuleOptionsSerialization.OptionsKey("startup") + ":Endpoint", Assert.Single(failure.Issues).Path);
        Assert.Equal(0, Assert.IsType<CheckedOptions>(observed).ValidationCount);
    }

    [Fact]
    public async Task StartupAndCachedOptionsRunCustomValidationOnce()
    {
        var builder = Builder();
        builder.Configuration[ModuleOptionsSerialization.OptionsKey("startup") + ":Endpoint"] = "https://example.test";
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var first = host.Services.GetRequiredService<IOptions<CheckedOptions>>().Value;
        var second = host.Services.GetRequiredService<IOptions<CheckedOptions>>().Value;
        Assert.Same(first, second);
        Assert.Equal(1, first.ValidationCount);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void OptionsEvaluationUsesTheSameCustomIssuesAsCompileAndBind()
    {
        var builder = Builder();
        builder.Configuration[ModuleOptionsSerialization.OptionsKey("startup") + ":Endpoint"] = "https://example.test";
        builder.Configuration[ModuleOptionsSerialization.OptionsKey("startup") + ":Reject"] = "true";
        using var host = builder.Build();
        var evaluated = Assert.Throws<ModuleValidationException>(() => host.Services.GetRequiredService<IOptions<CheckedOptions>>().Value);
        var compiled = Assert.Throws<ModuleValidationException>(() => ModuleOptionsSerialization.Compile<CheckedModule, CheckedOptions>(new() { Endpoint = new("https://example.test"), Reject = true }));
        var bound = Assert.Throws<ModuleValidationException>(() => builder.Configuration.GetModuleOptions<CheckedOptions>("startup"));
        Assert.Equal(compiled.Issues, evaluated.Issues);
        Assert.Equal(bound.Issues, evaluated.Issues);
    }

    private static HostApplicationBuilder Builder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        // Exercise the public module registration without registering or starting an Orleans silo.
        new RegistrationBuilder(builder.Services, builder.Configuration).AddIntoChatOS([new CheckedModule()]);
        return builder;
    }

    private sealed class RegistrationBuilder(IServiceCollection services, IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfiguration Configuration { get; } = configuration;
    }

    public sealed class CheckedModule() : Module<CheckedOptions>(new("startup"))
    {
    }

    public sealed class CheckedOptions : IModuleOptions
    {
        [Setting(Required = true)] public Uri? Endpoint { get; set; }
        public bool Reject { get; set; }
        public int ValidationCount { get; private set; }
        public void Validate()
        {
            ValidationCount++;
            if (Reject) { throw new ModuleValidationException([new("Recipients[0]", "address", "A valid address is required.")]); }
        }
    }
}
