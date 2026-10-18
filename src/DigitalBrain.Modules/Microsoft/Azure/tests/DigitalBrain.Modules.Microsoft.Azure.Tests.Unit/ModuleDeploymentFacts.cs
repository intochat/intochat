using DigitalBrain.Framework.Modules;
using DigitalBrain.Microsoft.Azure;
using DigitalBrain.Microsoft.Azure.Install;
using DigitalBrain.Microsoft.CSharp;

namespace DigitalBrain.Modules.Microsoft.Azure.Tests.Unit;

public sealed class ModuleDeploymentFacts
{
    private const string Subscription = "00000000-0000-0000-0000-000000000001";

    private static readonly InstallConfig Config = InstallConfig.Parse("""
        {"subscription":"00000000-0000-0000-0000-000000000001","location":"westeurope","prefix":"intochat","env":"test","osImage":"ghcr.io/intochat/os:1.0.0",
         "registryServer":"intochat.azurecr.io",
         "modules":{"csharp":{"sandboxImage":"intochat.azurecr.io/sandbox:1","sessionExecutorRoleId":"0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0","sandboxSessions":"50"}}}
        """);

    [Fact]
    public void A_deployment_in_the_assembly_of_a_composed_module_is_found_under_that_module()
    {
        var found = ModuleDeployments.Discover([new CSharpModule()]);

        var binding = Assert.Single(found);
        Assert.Equal("csharp", binding.ModuleId);
        Assert.IsType<CSharpDeployment>(binding.Deployment);
    }

    [Fact]
    public void A_composed_module_without_a_deployment_contributes_none()
    {
        Assert.Empty(ModuleDeployments.Discover([new AzureModule()]));
    }

    [Fact]
    public void A_deployment_for_a_module_that_is_not_composed_fails_loudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModuleDeployments.Discover([new HostModule()]));

        Assert.Contains(nameof(OrphanModule), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csharp_deployment_emits_network_environment_pool_and_grant_with_deterministic_names()
    {
        var specs = new List<ResourceSpec>();
        var context = Context(specs);

        await new CSharpDeployment().Deploy(context);

        Assert.Equal(
            [
                (ResourceKinds.NetworkSecurityGroup, "intochat-test-sandbox"),
                (ResourceKinds.VirtualNetwork, "intochat-test-sandbox-vnet"),
                (ResourceKinds.Subnet, "intochat-test-sandbox"),
                (ResourceKinds.ManagedEnvironment, "intochat-test-sandbox"),
                (ResourceKinds.SessionPool, "intochat-test-sandbox"),
                (ResourceKinds.RoleGrant, "intochat-test-csharp-sandbox-sessions"),
                (ResourceKinds.Secret, CSharpDeployment.RunTokenKeySecret),
            ],
            specs.Select(spec => (spec.Kind, spec.Name)));
        Assert.All(specs, spec =>
        {
            Assert.Equal("intochat-test-rg", spec.ResourceGroup);
            Assert.Equal("csharp", spec.Owner);
            Assert.Equal("test", spec.Env);
        });
    }

    [Fact]
    public async Task The_csharp_network_lets_sessions_out_on_https_only_inside_a_delegated_subnet()
    {
        var specs = new List<ResourceSpec>();

        await new CSharpDeployment().Deploy(Context(specs));

        var group = specs.OfType<NetworkSecurityGroupSpec>().Single();
        Assert.Equal(["allow-https-out", "deny-other-out"], group.Rules.Select(rule => rule.Name));
        Assert.Equal("443", group.Rules[0].DestinationPortRange);
        var subnet = specs.OfType<SubnetSpec>().Single();
        Assert.Equal("10.0.10.0/23", subnet.AddressPrefix);
        Assert.Equal("Microsoft.App/environments", subnet.DelegationService);
        Assert.Equal(group.Name, subnet.NetworkSecurityGroup);
        Assert.Contains("/subnets/intochat-test-sandbox", specs.OfType<ManagedEnvironmentSpec>().Single().SubnetId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csharp_pool_runs_the_sandbox_image_as_the_runtime_identity_with_the_configured_capacity()
    {
        var specs = new List<ResourceSpec>();

        await new CSharpDeployment().Deploy(Context(specs));

        var pool = specs.OfType<SessionPoolSpec>().Single();
        Assert.Equal("intochat.azurecr.io/sandbox:1", pool.Image);
        Assert.Equal("intochat-test-id", pool.Identity);
        Assert.Equal("intochat.azurecr.io", pool.RegistryServer);
        Assert.Equal(50, pool.MaxSessions);
        var grant = specs.OfType<RoleGrantSpec>().Single();
        Assert.Equal("0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0", grant.RoleDefinitionId);
        Assert.Equal("intochat-test-id", grant.Principal);
        Assert.EndsWith("/sessionPools/intochat-test-sandbox", grant.Scope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csharp_deployment_hands_the_app_its_pool_endpoint_and_a_run_token_key_that_a_later_install_keeps()
    {
        var context = Context([]);

        await new CSharpDeployment().Deploy(context);

        Assert.Equal(
            $"https://westeurope.dynamicsessions.io/subscriptions/{Subscription}/resourceGroups/intochat-test-rg/sessionPools/intochat-test-sandbox",
            Assert.Single(context.RuntimeVariables).Value);
        var secret = Assert.Single(context.RuntimeSecrets);
        Assert.Equal("intochat-test-kv", secret.Vault);
        Assert.EndsWith("RunTokenKey", secret.Variable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_csharp_deployment_needs_its_image()
    {
        var context = Context([], ModuleSettings.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CSharpDeployment().Deploy(context));
    }

    [Fact]
    public async Task The_dry_run_lists_the_module_resources_after_the_foundation_and_before_the_os_app()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(directory.FullName, "test.json");
            await File.WriteAllTextAsync(file, ConfigJson, TestContext.Current.CancellationToken);
            var arm = new RecordingAzure();
            var output = new StringWriter();

            var code = await AzureInstaller.Run(["install", "--env", "test", "--config", file, "--dry-run"], arm, [new CSharpModule()], output, new StringWriter(), TestContext.Current.CancellationToken);

            Assert.Equal(0, code);
            Assert.Empty(arm.Ensured);
            var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            string[] module =
            [
                "ensure network-security-group intochat-test-sandbox in intochat-test-rg",
                "ensure virtual-network intochat-test-sandbox-vnet in intochat-test-rg",
                "ensure subnet intochat-test-sandbox in intochat-test-rg",
                "ensure managed-environment intochat-test-sandbox in intochat-test-rg",
                "ensure session-pool intochat-test-sandbox in intochat-test-rg",
                "ensure role-grant intochat-test-csharp-sandbox-sessions in intochat-test-rg",
                "ensure secret csharp-run-token-key in intochat-test-rg",
            ];
            Assert.Equal("ensure secret master-key in intochat-test-rg", lines[^(module.Length + 2)]);
            Assert.Equal(module, lines[^(module.Length + 1)..^1]);
            Assert.Equal("ensure container-app intochat-test-os in intochat-test-rg", lines[^1]);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task An_install_ensures_the_module_resources_after_the_foundation_and_gives_the_app_what_they_asked_for()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(directory.FullName, "test.json");
            await File.WriteAllTextAsync(file, ConfigJson, TestContext.Current.CancellationToken);
            var secrets = Path.Combine(directory.FullName, "secrets.json");
            await File.WriteAllTextAsync(secrets, "{\"master-key\":\"k\"}", TestContext.Current.CancellationToken);
            var arm = new RecordingAzure();

            var code = await AzureInstaller.Run(["install", "--env", "test", "--config", file, "--secrets-from", secrets], arm, [new CSharpModule()], new StringWriter(), new StringWriter(), TestContext.Current.CancellationToken);

            Assert.Equal(0, code);
            var kinds = arm.Ensured.Select(spec => spec.Kind).ToArray();
            Assert.True(Array.IndexOf(kinds, ResourceKinds.SessionPool) > Array.IndexOf(kinds, ResourceKinds.Secret));
            var app = Assert.IsType<ContainerAppSpec>(arm.Ensured[^1]);
            Assert.Contains(app.Variables, pair => pair.Key.EndsWith("SessionPoolEndpoint", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, app.Secrets.Length);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task A_module_deployment_that_fails_stops_the_install_with_the_module_named()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(directory.FullName, "test.json");
            await File.WriteAllTextAsync(file, ConfigJson.Replace("sandboxImage", "unused", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            var error = new StringWriter();

            var code = await AzureInstaller.Run(["install", "--env", "test", "--config", file, "--dry-run"], new RecordingAzure(), [new CSharpModule()], new StringWriter(), error, TestContext.Current.CancellationToken);

            Assert.Equal(1, code);
            Assert.Contains("sandboxImage", error.ToString(), StringComparison.Ordinal);
        }
        finally { directory.Delete(true); }
    }

    private const string ConfigJson = """
        {"subscription":"00000000-0000-0000-0000-000000000001","location":"westeurope","prefix":"intochat","env":"test","osImage":"ghcr.io/intochat/os:1.0.0",
         "modules":{"csharp":{"sandboxImage":"intochat.azurecr.io/sandbox:1","sessionExecutorRoleId":"0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0"}}}
        """;

    private static ModuleDeploymentContext Context(List<ResourceSpec> specs, ModuleSettings? settings = null)
    {
        Task<ResourceRef> Ensure(ResourceSpec spec, CancellationToken token)
        {
            specs.Add(spec);
            return Task.FromResult(new ResourceRef(ResourceIds.For(Subscription, spec), spec.Kind, spec.Name, spec.ResourceGroup));
        }

        return new ModuleDeploymentContext(
            new DeploymentTarget(Subscription, "westeurope", "intochat", "test"),
            InstallPlan.Refs(Config),
            "csharp",
            settings ?? Config.SettingsFor("csharp"),
            Ensure,
            TestContext.Current.CancellationToken);
    }

    public sealed class OrphanModule;

    private sealed class HostModule() : Module(new ModuleId("host"))
    {
        public override void Configure(ISiloBuilder silo) { }
    }

    public sealed class OrphanDeployment : ModuleDeployment<OrphanModule>
    {
        public override Task Deploy(ModuleDeploymentContext context) => Task.CompletedTask;
    }
}
