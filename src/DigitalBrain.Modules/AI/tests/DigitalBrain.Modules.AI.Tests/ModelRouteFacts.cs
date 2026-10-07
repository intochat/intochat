using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class ModelRouteFacts
{
    [Fact]
    public async Task TheModelCatalogRouteIsGatedAndCarriesNoEndpointsOrSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        const string apiKey = "model-catalog-secret-canary";
        await using var brain = await ModuleTest.Create().WithModule<AIModule>()
            .WithExecution(new()
            {
                PrivateConfiguration = new Dictionary<string, string?>
                {
                    [IntegrationSchema.Key("openai", "ApiKey")] = apiKey,
                    [AuthOptions.PostureKey] = "Secured",
                    [AuthOptions.Key(nameof(AuthOptions.Username))] = "owner",
                    [AuthOptions.Key(nameof(AuthOptions.Password))] = "catalog-test-password",
                },
            })
            .ConfigureSilo(silo => silo.Services.Configure<AIOptions>(ai => ai.WithLlm<IGpt56Luna>()))
            .WithHttpEdge()
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
