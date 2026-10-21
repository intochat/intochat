using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using IntoChat.OS.Microsoft.Azure;

namespace IntoChat.OS.Modules.Microsoft.Azure.Tests.Unit;

public sealed class ArmRepeatabilityFacts
{
    [Fact]
    public async Task Identical_secret_does_not_write_another_version_but_a_changed_value_does()
    {
        using var handler = new AzureHttp();
        using var client = new HttpClient(handler);
        var arm = new ArmAzure(new Credential(), new HttpClientTransport(client));
        var secret = new SecretSpec { Name = "google-secret", Vault = "test-kv", Value = "first-value", Overwrite = true, ResourceGroup = "rg", Location = "polandcentral" };

        await arm.Ensure(AzureHttp.Subscription, secret, TestContext.Current.CancellationToken);
        await arm.Ensure(AzureHttp.Subscription, secret, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.SecretWrites);

        await arm.Ensure(AzureHttp.Subscription, secret with { Value = "second-value" }, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.SecretWrites);
        await arm.Ensure(AzureHttp.Subscription, secret with { Value = "third-value", Overwrite = false }, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.SecretWrites);
    }

    [Fact]
    public async Task Identical_container_template_does_not_send_another_update_but_a_changed_image_does()
    {
        using var handler = new AzureHttp();
        using var client = new HttpClient(handler);
        var arm = new ArmAzure(new Credential(), new HttpClientTransport(client));
        var app = new ContainerAppSpec
        {
            Name = "test-api", ResourceGroup = "rg", Location = "polandcentral", Environment = "test-env", Identity = "test-id", Image = "acr.azurecr.io/api:1",
            Variables = new Dictionary<string, string> { ["B"] = "second", ["A"] = "first" },
        };

        await arm.Ensure(AzureHttp.Subscription, app, TestContext.Current.CancellationToken);
        await arm.Ensure(AzureHttp.Subscription, app with { Variables = new Dictionary<string, string> { ["A"] = "first", ["B"] = "second" } }, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.AppWrites);

        await arm.Ensure(AzureHttp.Subscription, app with { Image = "acr.azurecr.io/api:2" }, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.AppWrites);
        await arm.Ensure(AzureHttp.Subscription, app with { Image = "acr.azurecr.io/api:2", Variables = new Dictionary<string, string>() }, TestContext.Current.CancellationToken);
        Assert.Equal(3, handler.AppWrites);
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("offline-test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    // Azure SDK requests cross a real HTTP transport; only the remote service is replaced.
    private sealed class AzureHttp : HttpMessageHandler
    {
        public const string Subscription = "00000000-0000-0000-0000-000000000001";
        private const string Group = "/subscriptions/" + Subscription + "/resourceGroups/rg";
        private JsonObject? _app;
        private string? _secret;
        public int SecretWrites { get; private set; }
        public int AppWrites { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            JsonObject? result;
            if (uri.Host == "test-kv.vault.azure.net")
            {
                if (request.Headers.Authorization is null)
                {
                    var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    challenge.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer authorization=\"https://login.windows.net/00000000-0000-0000-0000-000000000001\", resource=\"https://vault.azure.net\"");
                    return challenge;
                }
                if (request.Method == HttpMethod.Put)
                {
                    SecretWrites++;
                    _secret = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["value"]!.GetValue<string>();
                }
                result = _secret is null ? null : new JsonObject { ["value"] = _secret, ["id"] = "https://test-kv.vault.azure.net/secrets/google-secret/version", ["attributes"] = new JsonObject { ["enabled"] = true } };
            }
            else if (path.Equals(Group, StringComparison.OrdinalIgnoreCase))
            {
                result = new JsonObject { ["id"] = Group, ["name"] = "rg", ["location"] = "polandcentral", ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded" } };
            }
            else if (path.Contains("/managedEnvironments/", StringComparison.OrdinalIgnoreCase))
            {
                result = new JsonObject { ["id"] = Group + "/providers/Microsoft.App/managedEnvironments/test-env", ["name"] = "test-env", ["location"] = "polandcentral", ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded" } };
            }
            else if (path.Contains("/userAssignedIdentities/", StringComparison.OrdinalIgnoreCase))
            {
                result = new JsonObject { ["id"] = Group + "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-id", ["name"] = "test-id", ["location"] = "polandcentral", ["properties"] = new JsonObject { ["clientId"] = Subscription, ["principalId"] = Subscription } };
            }
            else if (path.Contains("/containerApps/", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method == HttpMethod.Put)
                {
                    AppWrites++;
                    _app = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                    _app["id"] = Group + "/providers/Microsoft.App/containerApps/test-api";
                    _app["name"] = "test-api";
                    _app["properties"]!["provisioningState"] = "Succeeded";
                }
                result = _app;
            }
            else { throw new InvalidOperationException($"Unexpected Azure test request: {request.Method} {uri.GetLeftPart(UriPartial.Path)}"); }

            return new HttpResponseMessage(result is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(result?.ToJsonString() ?? "{\"error\":{\"code\":\"NotFound\",\"message\":\"not found\"}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
