using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Authority.Identity.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Integration;

public sealed class ModelRouteFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task TheModelCatalogRouteIsGatedAndCarriesNoEndpointsOrSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        const string apiKey = "model-catalog-secret-canary";
        await using var brain = await TestBrain.Create().WithModule<AIModule>()
            .WithProvider("OpenAI", apiKey: apiKey)
            .WithConfiguration(AuthOptions.PostureKey, "Secured")
            .WithConfiguration(AuthOptions.Key(nameof(AuthOptions.Username)), "owner")
            .WithConfiguration(AuthOptions.Key(nameof(AuthOptions.Password)), "catalog-test-password")
            .WithOptions<AIModule, AIOptions>(ai => ai.WithLlm<IGpt56Luna>())
            .StartAsync(ct);

        using var anonymous = await brain.HttpClient.GetAsync("/ai/models", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        brain.HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("owner:catalog-test-password")));
        using var response = await brain.HttpClient.GetAsync("/ai/models", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(apiKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoint", body, StringComparison.OrdinalIgnoreCase);
        using var catalog = JsonDocument.Parse(body);
        Assert.True(catalog.RootElement.GetProperty("automatic").GetProperty("available").GetBoolean());
        Assert.Contains(catalog.RootElement.GetProperty("models").EnumerateArray(),
            model => model.GetProperty("id").GetString() == "preset:IGpt56Luna");
    }
}
