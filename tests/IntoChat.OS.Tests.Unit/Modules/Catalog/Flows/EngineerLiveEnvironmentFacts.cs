using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// What "a live environment is configured" means for the product's own live verifier: a candidate verifier registered in the
// services, or a live endpoint in configuration. Nothing else, and never an argument of a call.
public sealed class EngineerLiveEnvironmentFacts
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(static pair => pair.Key, static pair => pair.Value)).Build();

    [Fact]
    public void With_nothing_registered_and_nothing_set_no_environment_is_configured()
    {
        using var bare = new EngineerLiveVerifier();
        using var empty = new EngineerLiveVerifier(Configuration(("IntoChatOS:Engineer:LiveBaseUrl", " "), ("IntoChatOS:Engineer:McpEndpoint", "http://localhost:1/mcp")));
        using var services = new ServiceCollection().AddSingleton(Configuration()).BuildServiceProvider();
        using var fromServices = new EngineerLiveVerifier(services: services);

        Assert.False(bare.Configured);
        Assert.False(empty.Configured);
        Assert.False(fromServices.Configured);
    }

    [Theory]
    [InlineData("IntoChatOS:Engineer:LiveBaseUrl")]
    [InlineData("IntoChatOS:Engineer:LoopbackHealthUrl")]
    public void A_live_endpoint_in_configuration_is_a_configured_environment(string key)
    {
        using var verifier = new EngineerLiveVerifier(Configuration((key, "http://127.0.0.1:1")));

        Assert.True(verifier.Configured);
    }

    [Fact]
    public void A_candidate_verifier_in_the_services_is_a_configured_environment()
    {
        static Task<EngineerLiveReport> Candidate(EngineerLiveContext context, CancellationToken ct) => Task.FromResult(new EngineerLiveReport());
        using var services = new ServiceCollection().AddSingleton<Func<EngineerLiveContext, CancellationToken, Task<EngineerLiveReport>>>(Candidate).BuildServiceProvider();
        using var verifier = new EngineerLiveVerifier(services: services);

        Assert.True(verifier.Configured);
    }
}
