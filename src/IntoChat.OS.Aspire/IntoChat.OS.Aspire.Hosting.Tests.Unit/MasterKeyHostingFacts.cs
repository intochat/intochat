using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class MasterKeyHostingFacts
{
    [Fact]
    public async Task Referencing_a_brain_supplies_the_same_master_key_to_each_runtime_but_not_clients()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[$"Parameters:modules-{MasterKey.ParameterName}"] = "hosting test master key";
        var brain = builder.AddIntoChatOS("modules", options: new());
        var first = builder.AddExecutable("first", "unused", ".").WithReference(brain);
        var second = builder.AddExecutable("second", "unused", ".").WithReference(brain);
        var client = builder.AddExecutable("client", "unused", ".").WithReference(brain.AsClient());

        var parameter = builder.Resources.OfType<ParameterResource>().Single(resource => resource.Name == $"modules-{MasterKey.ParameterName}");
        var expected = await parameter.GetValueAsync(TestContext.Current.CancellationToken);
        var firstEnvironment = await EnvironmentOf(builder, first.Resource);
        var secondEnvironment = await EnvironmentOf(builder, second.Resource);
        Assert.Equal(expected, firstEnvironment[IntoChatOSNames.MasterKeyEnvironmentVariable]);
        Assert.Equal(expected, secondEnvironment[IntoChatOSNames.MasterKeyEnvironmentVariable]);
        Assert.DoesNotContain(IntoChatOSNames.MasterKeyEnvironmentVariable, (await EnvironmentOf(builder, client.Resource)).Keys);
    }

    [Fact]
    public void AConfiguredLegacyMasterKeyMustBeMigratedExplicitly()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[$"Parameters:{MasterKey.ParameterName}"] = "existing key must survive";
        Assert.Throws<InvalidOperationException>(() => builder.AddIntoChatOS("upgraded"));
    }

    [Fact]
    public void SiloDiscoveryUsesCompositionRatherThanEndpointNames()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var impostor = builder.AddExecutable("impostor", "unused", ".").WithHttpEndpoint(name: "http").WithEndpoint(name: "orleans-silo");
        Assert.False(SiloHosts.IsSiloHost(impostor.Resource));
        var brain = builder.AddIntoChatOS("brain");
        var silo = builder.AddExecutable("silo", "unused", ".").WithReference(brain);
        Assert.True(SiloHosts.IsSiloHost(silo.Resource));
    }

    [Fact]
    public void Publishing_a_brain_requires_a_master_key_secret_without_a_generated_default()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--publisher", "manifest"],
            DisableDashboard = true,
        });
        builder.AddIntoChatOS("modules", options: new());

        var parameter = builder.Resources.OfType<ParameterResource>().Single(resource => resource.Name == $"modules-{MasterKey.ParameterName}");
        Assert.True(parameter.Secret);
        Assert.Null(parameter.Default);
    }

    private static async Task<IReadOnlyDictionary<string, string>> EnvironmentOf(IDistributedApplicationBuilder builder, IResource resource)
    {
        var configuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .AddExecutionConfigurationGatherer(new MasterKeyEnvironment())
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
        return configuration.EnvironmentVariables.ToDictionary();
    }

    // Other environment entries depend on live endpoints; this fact only resolves the master key.
    private sealed class MasterKeyEnvironment : IExecutionConfigurationGatherer
    {
        public ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource,
            ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
        {
            var present = context.EnvironmentVariables.TryGetValue(IntoChatOSNames.MasterKeyEnvironmentVariable, out var value);
            context.EnvironmentVariables.Clear();
            if (present) { context.EnvironmentVariables.Add(IntoChatOSNames.MasterKeyEnvironmentVariable, value!); }
            return ValueTask.CompletedTask;
        }
    }
}
