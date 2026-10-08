using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Aspire.Hosting;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.E2E;
using DigitalBrain.Testing.Model;

namespace DigitalBrain.OS.Tests.E2E;

// The product boot with a local System One endpoint: the fake model answers /v1/systemone for
// routing and the arg-fill chat, so a request routes through Jev and carries itself out offline.
public sealed class RouterHostFixture : ReferenceBrainFixture
{
    protected override Task<E2EBrain> StartBrainAsync(CancellationToken cancellationToken)
    {
        var root = new Uri(Model.Endpoint, "/").AbsoluteUri.TrimEnd('/');
        Model.ChatFallback = Fill;
        return ReferenceBrain.Create(privateConfiguration: new Dictionary<string, string?>
        {
            [IntegrationSchema.Key("typesafe", "ApiKey")] = "fixture-key",
            [IntegrationSchema.Key("typesafe", "Endpoint")] = root,
        })
            .ConfigureModule<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .ConfigureModule<FlutterModule, FlutterModuleOptions>(flutter => flutter.RunWebApp())
            .WithHostConfiguration(FlutterHostOptions.WebContainerKey, "true")
            .WithBrowser(new() { PrimarySession = false, AssertionTimeout = TimeSpan.FromMinutes(2) })
            .WithStartupTimeout(TimeSpan.FromMinutes(15))
            .ExportingTelemetryTo(Collector)
            .StartAsync(cancellationToken);
    }

    // The fill prompt carries the brain scope; answer with a valid timer call so the demo is
    // deterministic. A request that is not the fill prompt keeps the echo default.
    private static FakeReply? Fill(JsonElement request)
    {
        var text = string.Concat(request.GetProperty("messages").EnumerateArray().SelectMany(message =>
            message.GetProperty("content").ValueKind == JsonValueKind.String
                ? [message.GetProperty("content").GetString() ?? ""]
                : message.GetProperty("content").EnumerateArray()
                    .Where(part => part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString() ?? "")));
        if (!text.Contains("Fill one neuron call", StringComparison.Ordinal)) { return null; }
        var scope = Regex.Match(text, @"brain scope is (\S+)\.") is { Success: true } match ? match.Groups[1].Value : "scope";
        return FakeReply.Text("{\"key\":\"" + scope + "/timer\",\"arguments\":[\"00:05:00\",null]}");
    }
}
